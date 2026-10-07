using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq;
using Furion.DependencyInjection;
using GONES.Model.Pg;
using GONES.Web.Helpers;

namespace GONES.Web.Services
{
    /// <summary>
    /// Stock / batch service - single source of truth (redesigned 2026-09-07).
    ///
    /// Core rule: the BILL IS THE LEDGER. A batch balance is never maintained by hand,
    /// it is always derived:
    ///
    ///     qty(batch)  = SUM(t_PMS_StockBillEntry.FNum * FROB) over audited bills
    ///     qty(item)   = SUM(t_PMS_StockBillEntry.FNum * FROB) over audited bills
    ///
    ///     FROB = +1 in-stock, -1 out-stock (column on t_PMS_StockBillEntry, added 2026-09-07)
    ///     only bills with t_PMS_StockBill.FState"" = true count.
    ///
    /// Therefore:
    ///   - t_PMS_BatchNoStock keeps BATCH MASTER DATA only (no, batch no, item, date, price,
    ///     state). Its FBatchNum column is a CACHE, rebuilt by RecalcBatch - never trusted,
    ///     never incremented by hand.
    ///   - t_PMS_BatchNoStockEntry was DROPPED (2026-09-07). It duplicated t_PMS_StockBillEntry
    ///     (it even carried FStockBillEntryID pointing at it).
    ///   - t_ERP_ITEM.StockQuantity is also a CACHE, rebuilt by RecalcItem. It exists so the item
    ///     list page can read one column instead of aggregating.
    ///   - Un-audit no longer subtracts anything; it clears the batch stamp and re-derives.
    ///
    /// xnkc keeps its old meaning (virtual stock for the e-commerce side) and is NEVER touched.
    ///
    /// Batch no rule: yyyyMMdd + class code(2) + daily sequence(3), e.g. 2026090701001.
    /// Class code comes from t_ERP_ITEMCLASS.lb: 01 raw, 02 packing, 03 product,
    /// 04 subsidy, 05 wip, 06 semi-finished, else 00.
    ///
    /// Concurrency: batch no / batch ids are Max+1, so "read max -> build -> SaveChanges"
    /// must hold a process-wide lock (DbContext is scoped; SaveChanges would otherwise land
    /// after the lock is released).
    /// </summary>
    public class StockService : ISingleton
    {
        private static readonly object BatchLock = new object();

        /// <summary>
        /// Process-wide lock for t_PMS_StockBill.FInterID (Max+1). EVERY bill module
        /// (StockBill / WarehousePick / WarehouseReturn / DepartmentPick / DepartmentReturn /
        /// ProductIn / ProductLoss) writes into the SAME table, so they must share ONE lock --
        /// per-controller locks do not exclude each other and would hand out duplicate ids.
        /// </summary>
        public static readonly object BillNoLock = new object();

        /// <summary>FROB value for in-stock rows.</summary>
        public const short DirIn = 1;
        /// <summary>FROB value for out-stock rows.</summary>
        public const short DirOut = -1;

        // ------------------------------------------------------------------ stock-check derivation
        //
        // 仓库盘点(FB=4 / CKPDD) 是「工作表」而不是库存动作：它的明细行把实盘数量记在 FPlanNum、
        // FNum 恒为 0，因此对账本 6 处聚合（BatchQty / ItemQty / BatchQtyMap / BatchQtyLedger /
        // BatchesOfItem / RebuildItem）零贡献 —— 不需要改动任何账本 SQL。
        // 审核时按「实盘 − 账面」差额派生出真正的库存单据，见 DeriveStockCheckAudit。
        // 口径沿用旧系统 FrmStockCheck.cs:418（结存数量写入 FPlanNum）。

        /// <summary>FBillType of the stock-check sheet (legacy CKPDD 仓库盘点).</summary>
        public const int StockCheckBillType = 4;
        /// <summary>Derived 盘盈 bill (legacy PDYYD): counted &gt; book, stock goes UP as a new batch.</summary>
        public const int StockSurplusBillType = 7;
        /// <summary>Derived 盘亏 bill (legacy PDYKD): counted &lt; book, stock comes off existing batches.</summary>
        public const int StockLossBillType = 10;
        public const string StockSurplusBillNoPrefix = "PDYYD";
        public const string StockLossBillNoPrefix = "PDYKD";
        // 落库口径：FBillTypeEx 去「单」再截前 4 字（与 GonesPgDbContext.NormalizeBillTypeEx 一致）
        public const string StockSurplusBillTypeEx = "\u76d8\u70b9\u76c8\u4f59";   // 盘点盈余（原「盘点盈余单」）
        public const string StockLossBillTypeEx = "\u76d8\u70b9\u4e8f\u635f";      // 盘点亏损（原「盘点亏损单」）

        private const string C_Raw = "\u539f\u6750\u6599";         // 原材料
        private const string C_Pack = "\u5305\u88c5\u7269";        // 包装物
        private const string C_Product = "\u4ea7\u6210\u54c1";     // 产成品
        private const string C_Subsidy = "\u8f85\u6599";           // 辅料
        private const string C_Wip = "\u5728\u4ea7\u54c1";         // 在产品
        private const string C_Semi = "\u534a\u6210\u54c1";        // 半成品

        // ------------------------------------------------------------------ helpers

        /// <summary>Map t_ERP_ITEMCLASS.lb to the 2-digit class code inside the batch no.</summary>
        public static string ClassCodeOf(string lb)
        {
            if (string.IsNullOrWhiteSpace(lb)) return "00";
            var v = lb.Trim();
            if (v == C_Raw) return "01";
            if (v == C_Pack) return "02";
            if (v == C_Product) return "03";
            if (v == C_Subsidy) return "04";
            if (v == C_Wip) return "05";
            if (v == C_Semi) return "06";
            if (v.Contains(C_Raw)) return "01";
            if (v.Contains(C_Semi)) return "06";
            return "00";
        }

        /// <summary>Item class big-category name (t_ERP_ITEMCLASS.lb) for an item id.</summary>
        public static string ItemClassLb(GonesPgDbContext db, int? itemId)
        {
            if (!itemId.HasValue) return null;
            var clsId = db.t_ERP_ITEM.Where(i => i.ID == itemId.Value)
                                     .Select(i => i.CategoryId)
                                     .FirstOrDefault();
            if (!clsId.HasValue) return null;
            return db.t_ERP_ITEMCLASS.Where(c => c.ID == clsId.Value)
                                     .Select(c => c.TopClassName)
                                     .FirstOrDefault();
        }

        private static int NextBatchInterId(GonesPgDbContext db)
        {
            var max = db.t_PMS_BatchNoStock.Any() ? db.t_PMS_BatchNoStock.Max(b => b.FInterID) : 0;
            return max + 1;
        }

        /// <summary>Largest existing daily sequence for a prefix (yyyyMMdd + class code).</summary>
        private int NextBatchSeq(GonesPgDbContext db, string prefix)
        {
            var candidates = db.t_PMS_BatchNoStock
                               .Where(b => b.FBatchNo.StartsWith(prefix))
                               .Select(b => b.FBatchNo)
                               .ToList();
            int seq = 0;
            foreach (var no in candidates)
            {
                if (string.IsNullOrEmpty(no) || no.Length != prefix.Length + 3) continue;
                int n;
                if (int.TryParse(no.Substring(prefix.Length), out n) && n > seq) seq = n;
            }
            return seq;
        }

        /// <summary>
        /// yyyyMMdd + class(2) + daily sequence(3). Sequence = max existing sequence for the same
        /// date+class + 1; malformed rows are ignored instead of poisoning the counter.
        /// </summary>
        public string NextBatchNo(GonesPgDbContext db, DateTime date, string classCode)
        {
            var prefix = date.ToString("yyyyMMdd") + (classCode ?? "00");
            return prefix + (NextBatchSeq(db, prefix) + 1).ToString("000");
        }

        /// <summary>
        /// Quantity one entry row moves, i.e. FNum plus the three reason-split columns.
        /// Mirrors SqlEntryQty so C# validation and SQL aggregation never drift apart.
        /// </summary>
        public static decimal RowQty(t_PMS_StockBillEntry e)
        {
            if (e == null) return 0m;
            return (e.FNum ?? 0m) + (e.FNumExt1 ?? 0m) + (e.FNumExt2 ?? 0m) + (e.FNumExt3 ?? 0m);
        }

        /// <summary>
        /// Stamp entry FPrice/FAmount from the source batch (t_PMS_BatchNoStock.FPrice).
        /// Used by out-stock / return bills: a product moved from a priced batch carries the
        /// batch unit price, and FAmount = ledger qty (RowQty) * price. No-op when the entry
        /// has no batch, or the batch has no FPrice.
        /// </summary>
        public static void StampBatchPrice(GonesPgDbContext db, t_PMS_StockBillEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.FBatchNo)) return;
            var batch = db.t_PMS_BatchNoStock.FirstOrDefault(b => b.FBatchNo == e.FBatchNo);
            if (batch == null || !batch.FPrice.HasValue) return;
            e.FPrice = batch.FPrice;
            e.FAmount = Math.Round(RowQty(e) * batch.FPrice.Value, 2);
        }

        // ------------------------------------------------------------------ 来源行占用额度
        //
        // 三个模块都从一张「来源单据」导入明细，且都必须防止同一来源行被重复导入：
        //   FB=1 生产入库 (ProductIn)    来源 = 生产单行 / 仓库领料单(FB=8)
        //   FB=5 产品报损 (ProductLoss)  来源 = 仓库退料单(FB=3) 明细行
        //   FB=6 换货入库 (ExchangeIn)   来源 = 仓库退料单(FB=3) 明细行
        // 此前三个模块各写一份，改一处漏两处（2026-09-10 因此出现「换货入库草稿不占额度」
        // 可重复换货的漏洞）。现统一收敛到本节——新增第四个来源模块必须复用本节方法，
        // 不要另起一份。
        //
        // 两个语义必须分开，混用必然出 bug：
        //   includeDraft = true  →「已占用额度」：草稿也占额度。否则两张草稿可各导入同一
        //                         来源行并各保存一次，审核后合计超出来源量（重复入库/报废/换货）。
        //   includeDraft = false →「实际发生量」：只有审核才真正移动库存。用于推进上游单据
        //                         状态（如生产单 FStockStatus/FOrderStatus）——草稿不该让
        //                         生产单变成「已入库」。
        //
        // 数量口径统一用 RowQty（FNum+Ext1+Ext2+Ext3，恒正；方向由 FROB 在 SQL 层处理）。

        /// <summary>一行"来源占用"的已物化记录，供下面三种口径投影。</summary>
        private class UsageSourceRow
        {
            public int InterId;      // 占用方单据 ID（excludeInterId 用它排除自身）
            public int PickBillId;   // 来源单据 ID   (entry.FPickBillID)
            public int PickEntryId;  // 来源明细行 ID (entry.FPickEntryID)
            public int ItemId;
            public string BatchNo;   // 仅「商品+批次」口径使用；来源行口径恒为 ""
            public int ProduceId;
            public int BomId;
            public decimal Qty;      // RowQty（fourColumns=false 时仅 FNum）
        }

        private enum UsageSourceKind { PickBill, ProduceOrder }

        /// <summary>
        /// 取某单据类型下引用了 sourceIds 的明细行（含草稿或仅已审）。
        /// EF6 无法把 tuple/ValueTuple 常量送进 LINQ-to-Entities，故先按 primitive 列表过滤再物化。
        /// </summary>
        private static List<UsageSourceRow> LoadUsageRows(
            GonesPgDbContext db, int billType, UsageSourceKind kind,
            List<int> sourceIds, bool includeDraft)
        {
            if (sourceIds == null || sourceIds.Count == 0) return new List<UsageSourceRow>();

            var hq = db.t_PMS_StockBill.Where(h => h.FBillType == billType);
            if (!includeDraft) hq = hq.Where(h => (h.FState ?? false));
            var billIds = hq.Select(h => h.FInterID).ToList();
            if (billIds.Count == 0) return new List<UsageSourceRow>();

            var eq = db.t_PMS_StockBillEntry
                .Where(e => billIds.Contains(e.FInterID) && e.FItemID != null && e.FItemID.Value > 0);
            if (kind == UsageSourceKind.PickBill)
                eq = eq.Where(e => e.FPickBillID != null && sourceIds.Contains(e.FPickBillID.Value));
            else
                eq = eq.Where(e => e.FProduceID != null && sourceIds.Contains(e.FProduceID.Value));

            return eq.Select(e => new
            {
                e.FInterID,
                PickBillId = (int?)e.FPickBillID,
                PickEntryId = (int?)e.FPickEntryID,
                ItemId = (int?)e.FItemID,
                ProduceId = (int?)e.FProduceID,
                BomId = (int?)e.FBomId,
                e.FNum,
                e.FNumExt1,
                e.FNumExt2,
                e.FNumExt3
            })
            .ToList()
            .Select(x => new UsageSourceRow
            {
                InterId = x.FInterID,
                PickBillId = x.PickBillId ?? 0,
                PickEntryId = x.PickEntryId ?? 0,
                ItemId = x.ItemId ?? 0,
                ProduceId = x.ProduceId ?? 0,
                BomId = x.BomId ?? 0,
                Qty = (x.FNum ?? 0m) + (x.FNumExt1 ?? 0m) + (x.FNumExt2 ?? 0m) + (x.FNumExt3 ?? 0m),
            })
            .ToList();
        }

        /// <summary>按 key 累加，跳过 excludeInterId 指定的单据自身。</summary>
        private static Dictionary<TKey, decimal> SumUsage<TKey>(
            List<UsageSourceRow> rows, Func<UsageSourceRow, TKey> keySelector, int? excludeInterId)
        {
            var map = new Dictionary<TKey, decimal>();
            foreach (var r in rows)
            {
                if (excludeInterId.HasValue && r.InterId == excludeInterId.Value) continue;
                var k = keySelector(r);
                decimal cur;
                map.TryGetValue(k, out cur);
                map[k] = cur + r.Qty;
            }
            return map;
        }

        /// <summary>
        /// 【共享】按来源单据行 (FPickBillID, FPickEntryID) 累计占用量。
        /// 用于换货入库(FB=6) / 产品报损(FB=5) / 生产入库领料单来源(FB=1)。
        /// </summary>
        public static Dictionary<(int billId, int entryId), decimal> UsageByPickRow(
            GonesPgDbContext db, int billType, List<int> sourceBillIds,
            int? excludeInterId, bool includeDraft)
        {
            var rows = LoadUsageRows(db, billType, UsageSourceKind.PickBill, sourceBillIds, includeDraft);
            return SumUsage(rows, r => (r.PickBillId, r.PickEntryId), excludeInterId);
        }

        /// <summary>
        /// 【共享】按产成品 item 累计某领料单来源的占用量。
        /// 生产入库从仓库领料单导入时，一张领料单按 BOM 展开多个产出，故按 item 分组。
        /// </summary>
        public static Dictionary<int, decimal> UsageByPickItem(
            GonesPgDbContext db, int billType, int sourceBillId,
            int? excludeInterId, bool includeDraft)
        {
            var rows = LoadUsageRows(db, billType, UsageSourceKind.PickBill,
                new List<int> { sourceBillId }, includeDraft);
            return SumUsage(rows, r => r.ItemId, excludeInterId);
        }

        /// <summary>
        /// 【共享】按生产单行 (FProduceID, FBomId, FItemID) 累计占用量。
        /// </summary>
        public static Dictionary<(int produceId, int bomId, int itemId), decimal> UsageByProduceRow(
            GonesPgDbContext db, int billType, List<int> produceIds,
            int? excludeInterId, bool includeDraft)
        {
            var rows = LoadUsageRows(db, billType, UsageSourceKind.ProduceOrder, produceIds, includeDraft);
            return SumUsage(rows, r => (r.ProduceId, r.BomId, r.ItemId), excludeInterId);
        }

        // ------------------------------------------------------------------ 「商品+批次」口径
        //
        // 退料类单据（FB=2 部门退料 / FB=3 仓库退料）的超退保护不按来源行，而按
        // (商品, 批次) 累计：某批次一共被退了多少，不得超过该批次的已领量 / 现存量。
        //
        // 三种语义由 audited 参数区分，混用必然出 bug：
        //   audited = null  → 全部（已审 + 草稿）。用于「已占用额度」——草稿必须占额度，
        //                     否则两张草稿可各退同一批次的定量，审核后合计超退。
        //   audited = true  → 仅已审。用于「实际发生量」（已领量是物理事实，草稿未移动库存）。
        //   audited = false → 仅未审草稿。只给「现存量型」上限用，见 DraftUsageByItemBatch。

        /// <summary>
        /// 取某单据类型下涉及 itemIds 的明细行。
        /// EF6 无法把 tuple/ValueTuple 常量送进 LINQ-to-Entities，故先按 primitive 列表过滤再物化。
        /// fourColumns：退料的 FNum / FNumExt1/2/3（正常 / 厂家 / 人为 / 其它原因）是否合计。
        ///              仓库退料(FB=3) 为四列合计；部门退料(FB=2) 为单数量列，只取 FNum。
        /// </summary>
        private static List<UsageSourceRow> LoadItemBatchRows(
            GonesPgDbContext db, int billType, List<int> itemIds, bool? audited, bool fourColumns)
        {
            if (itemIds == null || itemIds.Count == 0) return new List<UsageSourceRow>();

            var hq = db.t_PMS_StockBill.Where(h => h.FBillType == billType);
            if (audited.HasValue)
                // 必须是 !(FState ?? false) 而不是 FState != true：后者在 SQL 里会退化成
                // "FState <> 1"，而 NULL <> 1 求值为 UNKNOWN，FState 为 NULL 的草稿行会被
                // 静默排除，超退保护形同虚设。ISNULL 写法与 C# 端语义一致。
                hq = audited.Value
                    ? hq.Where(h => (h.FState ?? false))
                    : hq.Where(h => !(h.FState ?? false));
            var billIds = hq.Select(h => h.FInterID).ToList();
            if (billIds.Count == 0) return new List<UsageSourceRow>();

            return db.t_PMS_StockBillEntry
                .Where(e => billIds.Contains(e.FInterID) && e.FItemID != null
                            && itemIds.Contains(e.FItemID.Value))
                .Select(e => new
                {
                    e.FInterID,
                    ItemId = (int?)e.FItemID,
                    e.FBatchNo,
                    e.FNum,
                    e.FNumExt1,
                    e.FNumExt2,
                    e.FNumExt3
                })
                .ToList()
                .Select(x => new UsageSourceRow
                {
                    InterId = x.FInterID,
                    ItemId = x.ItemId ?? 0,
                    BatchNo = x.FBatchNo ?? "",
                    Qty = fourColumns
                        ? (x.FNum ?? 0m) + (x.FNumExt1 ?? 0m) + (x.FNumExt2 ?? 0m) + (x.FNumExt3 ?? 0m)
                        : (x.FNum ?? 0m),
                })
                .ToList();
        }

        /// <summary>
        /// 【共享】按 (商品, 批次) 累计占用量。
        /// includeDraft = true → 已审 + 草稿（防重复占用）；false → 仅已审（实际发生量）。
        /// </summary>
        public static Dictionary<(int itemId, string batchNo), decimal> UsageByItemBatch(
            GonesPgDbContext db, int billType, List<int> itemIds,
            int? excludeInterId, bool includeDraft, bool fourColumns)
        {
            var rows = LoadItemBatchRows(db, billType, itemIds,
                includeDraft ? (bool?)null : true, fourColumns);
            return SumUsage(rows, r => (r.ItemId, r.BatchNo), excludeInterId);
        }

        /// <summary>
        /// 【共享】按 (商品, 批次) 累计「仅未审草稿」的占用量。
        ///
        /// 只给「现存量型」上限用（部门退料手工行）：现存量来自 ledger，其 SQL 为
        /// h.FState"" = true，已把已审退料扣减过了。此处若再减「全部含草稿退料」，已审那部分会
        /// 被重复扣减 → 可用量偏小 → 正常单据被误拒。故只能补减未审草稿这一份。
        /// </summary>
        public static Dictionary<(int itemId, string batchNo), decimal> DraftUsageByItemBatch(
            GonesPgDbContext db, int billType, List<int> itemIds,
            int? excludeInterId, bool fourColumns)
        {
            var rows = LoadItemBatchRows(db, billType, itemIds, false, fourColumns);
            return SumUsage(rows, r => (r.ItemId, r.BatchNo), excludeInterId);
        }

        // ------------------------------------------------------------------ 要货申请行计划占用
        //
        // 生产单从要货申请单引入产品时，需要知道"该要货行已被多少生产单计划占用"：
        //   used  = SUM(t_PMS_ProduceOrderProductEntry.FPlanNum)
        //           其中该明细来自「要货单(demand) + 要货行(demandEntry)」这一唯一组合
        //   remain = FQty - used（默认引入量取 Max(0, remain)，不防超产但避免负数默认值）
        //
        // 口径：t_PMS_ProduceOrderProductEntry.(FBillApplyInterID, FBillApplyEntryID) 复合引用
        // (t_PMS_GoodsApplyEntry.FInterID, t_PMS_GoodsApplyEntry.FEntryID)。即便 FEntryID 是 IDENTITY 全局唯一，
        // 仍显式用复合键匹配，避免任何跨单同号要货行的误判（不依赖 FEntryID 恰好唯一这一假设）。
        // 口径是"计划占用"不是"已生产"：只要被引入生产单（无论审核/入库）即计入。编辑时通过
        // excludeProduceInterId 排除自身，避免自己把自己的量算成已占用。

        public static Dictionary<(int, int), decimal> UsedPlanByApplyEntryIds(
            GonesPgDbContext db, List<(int ApplyInterId, int ApplyEntryId)> applyKeys, int? excludeProduceInterId)
        {
            if (applyKeys == null || applyKeys.Count == 0) return new Dictionary<(int, int), decimal>();
            var interIds = applyKeys.Select(k => k.ApplyInterId).Distinct().ToList();
            var entryIds = applyKeys.Select(k => k.ApplyEntryId).Distinct().ToList();
            var rows = db.t_PMS_ProduceOrderProductEntry
                .Where(e => e.FBillApplyInterID != null && e.FBillApplyEntryID != null
                         && interIds.Contains(e.FBillApplyInterID.Value)
                         && entryIds.Contains(e.FBillApplyEntryID.Value))
                .Select(e => new { InterId = e.FBillApplyInterID.Value, EntryId = e.FBillApplyEntryID.Value, Plan = e.FPlanNum ?? 0m })
                .ToList();
            // 用复合键精确匹配，剔除仅单列命中（跨单同号）的误命中。
            return rows.Where(r => applyKeys.Contains((r.InterId, r.EntryId)))
                .GroupBy(r => (r.InterId, r.EntryId))
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Plan));
        }

        // ------------------------------------------------------------------ 要货申请行导入状态回写
        //
        // 生产单保存 / 删除 / 审核 / 反审核后，统一刷新 t_PMS_GoodsApplyEntry.FStatus：
        //   0 未导入 / 1 部分导入 / 2 全部导入
        // 口径与 UsedPlanByApplyEntryIds 一致（"计划占用"，草稿也计入，不防超产下占用可≥申请量）。
        // 本方法只改内存实体，调用方须在同一个 DbContext / 事务内随后 SaveChanges 落库。
        // 入参为 (GoodsApplyEntry.FInterID, FEntryID) 复合键列表，按复合键精确匹配（不跨单）。
        public static void RewriteApplyEntryStatus(GonesPgDbContext db, List<(int, int)> applyKeys)
        {
            if (applyKeys == null || applyKeys.Count == 0) return;

            // 不排除任何生产单：要的是该行在所有生产单里的真实累计占用（含本单、含草稿）。
            var usedMap = UsedPlanByApplyEntryIds(db, applyKeys, null);
            var interIds = applyKeys.Select(k => k.Item1).Distinct().ToList();
            var entryIds = applyKeys.Select(k => k.Item2).Distinct().ToList();
            var rows = db.t_PMS_GoodsApplyEntry
                .Where(e => interIds.Contains(e.FInterID) && entryIds.Contains(e.FEntryID))
                .ToList();
            foreach (var row in rows.Where(r => applyKeys.Contains((r.FInterID, r.FEntryID))))
            {
                var used = usedMap.ContainsKey((row.FInterID, row.FEntryID)) ? usedMap[(row.FInterID, row.FEntryID)] : 0m;
                var applyQty = row.FQty ?? 0m;
                row.FStatus = used <= 0m ? (byte)0 : (used >= applyQty ? (byte)2 : (byte)1);
            }
        }

        // ------------------------------------------------------------------ derived qty
        //
        // Aggregation is raw SQL on purpose: the same LINQ projection returns the right number in
        // a fresh DbContext but came back empty inside the audit request (EF6 translation + local
        // state), so the ledger math is done by the database and never by the ORM.
        // SQL Server 2008 compatible (no STRING_AGG / no window functions).

        /// <summary>
        /// Ledger quantity of ONE entry row. FNum is the plain quantity; FNumExt1/2/3 are the
        /// reason-split columns used by return bills (vendor / human / other). All four are
        /// stored POSITIVE and the direction comes from FROB, so every ledger query has to sum
        /// all four -- otherwise a warehouse return would put stock back that the out-stock
        /// check can never see.
        /// COALESCE() on FNum matters: the column is nullable and a NULL would NULL the whole sum.
        /// </summary>
        private const string SqlEntryQty =
            "(COALESCE(e.\"f_num\",0) + COALESCE(e.\"f_num_ext1\",0) + COALESCE(e.\"f_num_ext2\",0) + COALESCE(e.\"f_num_ext3\",0))";

        // {0} = the value parameter slot consumed by Database.SqlQueryRaw's {0} -> @p0
        // substitution. Aggregates stay lock-free; the audit-path lock is taken separately
        // by the SqlLockEntries* pre-lock queries below (PG cannot put FOR UPDATE on the
        // same query level as SUM/GROUP BY).
        private const string SqlBatchQtyTpl = @"
SELECT COALESCE(SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                       ELSE " + SqlEntryQty + @" END), CAST(0 AS decimal(18,5))) AS ""Value""
FROM   ""t_pms_stock_bill_entry"" e
INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
WHERE  h.""f_state"" = true AND e.""f_batch_no"" = {0}";

        private const string SqlItemQtyTpl = @"
SELECT COALESCE(SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                       ELSE " + SqlEntryQty + @" END), CAST(0 AS decimal(18,5))) AS ""Value""
FROM   ""t_pms_stock_bill_entry"" e
INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
WHERE  h.""f_state"" = true AND e.""f_item_id"" = {0}";

        /// <summary>
        /// PostgreSQL replacement for the old SQL Server WITH (UPDLOCK) hint: PG rejects
        /// FOR UPDATE on a query level that aggregates, so the lock is taken by a separate
        /// pre-lock query that selects the qualifying ledger entry ids in deterministic
        /// global order (FEntryID). Plain FOR UPDATE (no OF clause) locks both the entry
        /// and the joined header rows -- the same coverage UPDLOCK used to give. A second
        /// auditor reading the same balance then waits until the first transaction ends
        /// instead of seeing (and over-spending) the same pre-audit number.
        /// Only used on audit / un-audit paths inside a stock transaction -- plain reads
        /// stay lock-free.
        /// </summary>
        private const string SqlLockEntriesByBatch = @"
SELECT e.""f_entry_id"" AS ""Value""
FROM   ""t_pms_stock_bill_entry"" e
INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
WHERE  h.""f_state"" = true AND e.""f_batch_no"" = {0}
ORDER  BY e.""f_entry_id""
FOR UPDATE";

        private const string SqlLockEntriesByItem = @"
SELECT e.""f_entry_id"" AS ""Value""
FROM   ""t_pms_stock_bill_entry"" e
INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
WHERE  h.""f_state"" = true AND e.""f_item_id"" = {0}
ORDER  BY e.""f_entry_id""
FOR UPDATE";

        private const string SqlLockEntriesByBatchIn = @"
SELECT e.""f_entry_id"" AS ""Value""
FROM   ""t_pms_stock_bill_entry"" e
INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
WHERE  h.""f_state"" = true AND e.""f_batch_no"" IN ({0})
ORDER  BY e.""f_entry_id""
FOR UPDATE";

        /// <summary>Fully enumerates the pre-lock query so the row locks are held.</summary>
        private static void LockLedgerEntries(GonesPgDbContext db, string lockSql, object[] parameters)
        {
            db.Database.SqlQueryRaw<int>(lockSql, parameters).ToList();
        }

        private const string SqlBatchQty = SqlBatchQtyTpl;
        private const string SqlItemQty = SqlItemQtyTpl;

        // Batch derived qty for MANY batches in one GROUP BY query (kills the per-row N+1 that
        // used to run one full aggregation per detail row). {0} = the comma-separated {N}
        // placeholder list, filled per chunk so the parameter count stays well under PG's
        // 65535-parameter limit.
        private const string SqlBatchQtyMapBody = @"
SELECT e.""f_batch_no"" AS ""FBatchNo"",
       SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                ELSE " + SqlEntryQty + @" END) AS q
FROM   ""t_pms_stock_bill_entry"" e
INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
WHERE  h.""f_state"" = true AND e.""f_batch_no"" IN ({0})
GROUP  BY e.""f_batch_no""";

        private static string BuildBatchQtyMapSql(int count)
        {
            var inList = string.Join(",", Enumerable.Range(0, count).Select(i => "{" + i + "}"));
            return string.Format(SqlBatchQtyMapBody, inList);
        }

        /// <summary>
        /// True (derived) remaining quantity of a batch. This is the truth.
        /// `forUpdate` pre-locks the ledger rows (see SqlLockEntriesByBatch) -- only
        /// meaningful inside a stock transaction.
        /// </summary>
        public decimal BatchQty(GonesPgDbContext db, string batchNo, bool forUpdate = false)
        {
            if (string.IsNullOrEmpty(batchNo)) return 0m;
            if (forUpdate) LockLedgerEntries(db, SqlLockEntriesByBatch, new object[] { batchNo });
            return db.Database.SqlQueryRaw<decimal>(SqlBatchQty, batchNo).First();
        }

        /// <summary>True (derived) stock of an item across all batches.</summary>
        public decimal ItemQty(GonesPgDbContext db, int itemId, bool forUpdate = false)
        {
            if (forUpdate) LockLedgerEntries(db, SqlLockEntriesByItem, new object[] { itemId });
            return db.Database.SqlQueryRaw<decimal>(SqlItemQty, itemId).First();
        }

        /// <summary>
        /// Derived remaining qty of EVERY batch in <paramref name=batchNos/> as a
        /// batchNo -> qty map, computed in one GROUP BY per chunk. Replaces the per-row
        /// BatchQty N+1 calls that used to run one full aggregation per detail row.
        /// Batches with no movement resolve to 0 (same as BatchQty for a missing batch).
        /// Chunk size 1000 keeps each IN-list well under PG's 65535-parameter limit.
        /// </summary>
        public Dictionary<string, decimal> BatchQtyMap(GonesPgDbContext db, IEnumerable<string> batchNos, bool forUpdate = false)
        {
            var list = (batchNos ?? Enumerable.Empty<string>())
                .Where(b => !string.IsNullOrEmpty(b))
                .Select(b => b.Trim())
                .Distinct()
                .ToList();
            var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (list.Count == 0) return result;
            const int chunk = 1000;
            for (int i = 0; i < list.Count; i += chunk)
            {
                var slice = list.GetRange(i, Math.Min(chunk, list.Count - i));
                var inList = string.Join(",", Enumerable.Range(0, slice.Count).Select(j => "{" + j + "}"));
                if (forUpdate)
                    LockLedgerEntries(db, string.Format(SqlLockEntriesByBatchIn, inList), slice.Cast<object>().ToArray());
                var sql = BuildBatchQtyMapSql(slice.Count);
                foreach (var r in db.Database.SqlQueryRaw<BatchLedgerRow>(sql, slice.ToArray()))
                    result[r.FBatchNo] = r.q;
            }
            return result;
        }

        public class BatchLedgerRow
        {
            public string FBatchNo { get; set; }
            public decimal q { get; set; }
        }

        public class BatchDriftRow
        {
            public decimal TotalTruth { get; set; }
            public int DriftCount { get; set; }
        }

        /// <summary>
        /// Cache-vs-ledger drift aggregate for an explicit batch-number set: total ledger truth plus
        /// the number of batches whose cached FBatchNum disagrees with it. The caller passes the
        /// batch numbers it already filtered in EF, so the filter definition stays single-sourced and
        /// only the heavy half -- the ledger GROUP BY left-joined to the batch master -- runs in SQL,
        /// returning one row per chunk. Callers used to pull every matching batch row into memory.
        /// Chunk size 1000 keeps each IN-list well under SQL Server's 2100-parameter limit.
        /// </summary>
        public BatchDriftRow BatchDrift(GonesPgDbContext db, IEnumerable<string> batchNos)
        {
            var total = new BatchDriftRow();
            var list = (batchNos ?? Enumerable.Empty<string>())
                .Where(b => !string.IsNullOrEmpty(b))
                .Select(b => b.Trim())
                .Distinct()
                .ToList();
            if (list.Count == 0) return total;

            const int chunk = 1000;
            for (int i = 0; i < list.Count; i += chunk)
            {
                var slice = list.GetRange(i, Math.Min(chunk, list.Count - i));
                var inList = string.Join(",", Enumerable.Range(0, slice.Count).Select(k => "{" + k + "}"));
                var sql = string.Format(SqlBatchDriftSummary, inList);
                var row = db.Database.SqlQueryRaw<BatchDriftRow>(sql, slice.ToArray()).FirstOrDefault();
                if (row != null)
                {
                    total.TotalTruth += row.TotalTruth;
                    total.DriftCount += row.DriftCount;
                }
            }
            return total;
        }

        private const string SqlBatchesOfItem = @"
  SELECT e.""f_batch_no"" AS ""FBatchNo"",
         SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                  ELSE  " + SqlEntryQty + @" END) AS q
  FROM   ""t_pms_stock_bill_entry"" e
  INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
  WHERE  h.""f_state"" = true AND e.""f_item_id"" = {0} AND COALESCE(e.""f_batch_no"", '') <> ''
  GROUP  BY e.""f_batch_no""
  HAVING SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                  ELSE  " + SqlEntryQty + @" END) > 0
  ORDER  BY e.""f_batch_no""";

        /// <summary>
        /// Batches with positive derived stock for one item (ledger truth, not cache),
        /// ordered by batch no ascending. Feeds the batch-number dropdown on pick forms.
        /// </summary>
        public List<BatchLedgerRow> BatchesOfItem(GonesPgDbContext db, int itemId)
        {
            return db.Database.SqlQueryRaw<BatchLedgerRow>(SqlBatchesOfItem, itemId).ToList();
        }

        /// <summary>
        /// Defence in depth for out-stock bills. The batch no reaches the server as free text,
        /// so a tampered (or simply mistyped) POST can pair item A with a batch that belongs to
        /// item B. The batch ledger is grouped by FBatchNo and the item ledger by FItemID, so
        /// such a row corrupts BOTH ledgers at once and -- because the bill is the ledger --
        /// nothing can detect or repair the damage afterwards.
        /// Returns null when every non-empty batch no really belongs to its own row's item.
        /// Batches that are not in the master table yet are skipped (audit covers those).
        /// </summary>
        public string FindBatchItemMismatch(GonesPgDbContext db, IEnumerable<(int itemId, string batchNo)> rows)
        {
            var pairs = (rows ?? Enumerable.Empty<(int, string)>())
                .Where(r => r.itemId > 0 && !string.IsNullOrWhiteSpace(r.batchNo))
                .Select(r => new { ItemId = r.itemId, BatchNo = r.batchNo.Trim() })
                .Distinct()
                .ToList();
            if (pairs.Count == 0) return null;

            var batchNos = pairs.Select(p => p.BatchNo).Distinct().ToList();
            var owners = db.t_PMS_BatchNoStock
                .WhereInChunks(batchNos, b => b.FBatchNo)
                .Select(b => new { b.FBatchNo, b.FItemID })
                .GroupBy(b => b.FBatchNo)
                .ToDictionary(g => g.Key, g => g.First().FItemID);

            foreach (var p in pairs)
            {
                if (!owners.TryGetValue(p.BatchNo, out var owner)) continue;
                if (owner.HasValue && owner.Value != p.ItemId)
                    return "\u6279\u6b21 " + p.BatchNo + " \u4e0d\u5c5e\u4e8e\u6240\u9009\u5546\u54c1\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u3002";   // 批次 X 不属于所选商品，请重新选择。
            }
            return null;
        }

        // ------------------------------------------------------------------ recalc (cache rebuild)

        /// <summary>Rebuild the FBatchNum cache of one batch from the ledger.</summary>
        public void RecalcBatch(GonesPgDbContext db, string batchNo)
        {
            if (string.IsNullOrEmpty(batchNo)) return;
            var b = db.t_PMS_BatchNoStock.FirstOrDefault(x => x.FBatchNo == batchNo);
            if (b == null) return;
            b.FBatchNum = BatchQty(db, batchNo);
        }

        /// <summary>Rebuild the FStockNum cache of one item from the ledger.</summary>
        public void RecalcItem(GonesPgDbContext db, int itemId)
        {
            var item = db.t_ERP_ITEM.FirstOrDefault(i => i.ID == itemId);
            if (item == null) return;
            item.StockQuantity = ItemQty(db, itemId);
        }

        /// <summary>Rebuild every cache touched by one bill (by bill FInterID).</summary>
        public void RecalcBill(GonesPgDbContext db, int interId)
        {
            var touched = db.t_PMS_StockBillEntry.Where(e => e.FInterID == interId).ToList();
            RecalcAffected(db, touched);
        }

        /// <summary>Rebuild every cache touched by the given bill rows, then commit.</summary>
        public void RecalcAffected(GonesPgDbContext db, IEnumerable<t_PMS_StockBillEntry> entries)
        {
            if (entries == null) return;
            var list = entries as IList<t_PMS_StockBillEntry> ?? entries.ToList();
            var batchNos = list.Select(e => e.FBatchNo)
                               .Where(s => !string.IsNullOrEmpty(s))
                               .Distinct()
                               .ToList();
            var itemIds = list.Select(e => e.FItemID)
                              .Where(i => i.HasValue)
                              .Select(i => i.Value)
                              .Distinct()
                              .ToList();

            // 批次缓存一次性取回（BatchQtyMap 内部是一条 GROUP BY / 每 1000 个一批），
            // 取代原先「每个批次一次聚合查询」的 N 次往返；口径与 RecalcBatch 完全一致
            // （同一条账本 SQL，无账本行的批次按 0 处理）。
            var batchQty = BatchQtyMap(db, batchNos);
            foreach (var no in batchNos)
            {
                var b = db.t_PMS_BatchNoStock.FirstOrDefault(x => x.FBatchNo == no);
                if (b == null) continue;
                b.FBatchNum = batchQty.ContainsKey(no) ? batchQty[no] : 0m;
            }
            foreach (var id in itemIds) RecalcItem(db, id);
            db.SaveChanges();
        }

        private const string SqlLedgerBatch = @"
  SELECT e.""f_batch_no"",
         SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                  ELSE  " + SqlEntryQty + @" END) AS q
  FROM   ""t_pms_stock_bill_entry"" e
  INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
  WHERE  h.""f_state"" = true AND COALESCE(e.""f_batch_no"", '') <> ''
  GROUP  BY e.""f_batch_no""";

        /// <summary>
        /// Cache-vs-ledger drift for an explicit batch list. Reuses SqlLedgerBatch so the ledger
        /// definition stays single-sourced; only the batch-number filter comes from the caller.
        /// </summary>
        private const string SqlBatchDriftSummary = @"
  SELECT COALESCE(SUM(t.truth), 0) AS ""TotalTruth"",
         COALESCE(SUM(t.drift), 0) AS ""DriftCount""
  FROM (
      SELECT COALESCE(l.q, 0) AS truth,
             CASE WHEN ABS(COALESCE(b.""f_batch_num"", 0) - COALESCE(l.q, 0)) > 0.0001 THEN 1 ELSE 0 END AS drift
      FROM   ""t_pms_batch_no_stock"" b
      LEFT   JOIN (" + SqlLedgerBatch + @") l ON l.""f_batch_no"" = b.""f_batch_no""
      WHERE  b.""f_batch_no"" IN ({0})
  ) t";

        private const string SqlLedgerItem = @"
  SELECT e.""f_item_id"",
         SUM(CASE WHEN e.""frob"" = -1 THEN -" + SqlEntryQty + @"
                  ELSE  " + SqlEntryQty + @" END) AS q
  FROM   ""t_pms_stock_bill_entry"" e
  INNER  JOIN ""t_pms_stock_bill"" h ON h.""f_inter_id"" = e.""f_inter_id""
  WHERE  h.""f_state"" = true AND e.""f_item_id"" IS NOT NULL
  GROUP  BY e.""f_item_id""";

        private const string SqlRebuildBatch = @"
UPDATE b SET ""f_batch_num"" = COALESCE(x.q, 0)
FROM   ""t_pms_batch_no_stock"" b
LEFT   JOIN (" + SqlLedgerBatch + @") x ON x.""f_batch_no"" = b.""f_batch_no"";";

        private const string SqlRebuildItem = @"
UPDATE i SET ""stock_quantity"" = COALESCE(x.q, 0)
FROM   ""t_erp_item"" i
LEFT   JOIN (" + SqlLedgerItem + @") x ON x.""f_item_id"" = i.""id"";";

        private const string SqlCountDrift = @"
SELECT (SELECT COUNT(*) FROM ""t_pms_batch_no_stock"" b
        LEFT JOIN (" + SqlLedgerBatch + @") x ON x.""f_batch_no"" = b.""f_batch_no""
        WHERE COALESCE(b.""f_batch_num"", 0) <> COALESCE(x.q, 0))
     + (SELECT COUNT(*) FROM ""t_erp_item"" i
        LEFT JOIN (" + SqlLedgerItem + @") x ON x.""f_item_id"" = i.""id""
        WHERE COALESCE(i.""stock_quantity"", 0) <> COALESCE(x.q, 0)) AS ""Value""";

        /// <summary>
        /// Full rebuild: every batch cache + every item cache, in two set-based statements.
        /// Use for repair / nightly check. Returns how many cache rows were out of sync.
        /// </summary>
        public int RecalcAll(GonesPgDbContext db)
        {
            // 两条重建 UPDATE 必须原子：中途失败会留下「批次缓存已重建、商品缓存未重建」的
            // 半更新状态 —— 而这恰恰是本方法要消灭的「缓存 vs 账本」漂移。整体包事务，
            // 失败即回滚，库里要么全旧、要么全新，绝不留半新半旧。
            // 兼容调用方已开事务的情况（并入外层，不另开不嵌套），避免 EF 的嵌套事务异常。
            // 注：全表重算是运维级操作，应在业务低谷执行（大表 UPDATE 会长时间持锁）。
            bool ownsTx = db.Database.CurrentTransaction == null;
            var tx = ownsTx ? db.Database.BeginTransaction() : null;
            try
            {
                int drift = db.Database.SqlQueryRaw<int>(SqlCountDrift).First();
                db.Database.ExecuteSqlRaw(SqlRebuildBatch);
                db.Database.ExecuteSqlRaw(SqlRebuildItem);
                if (ownsTx) tx.Commit();
                return drift;
            }
            finally
            {
                if (ownsTx) tx.Dispose();
            }
        }

        // ------------------------------------------------------------------ transaction

        /// <summary>
        /// Name of the SQL Server application lock that serialises every stock-moving action.
        /// sp_getapplock with LockOwner='Transaction' is released automatically on COMMIT or
        /// ROLLBACK, so it can never leak even if a request dies mid-flight.
        /// </summary>
        public const string StockLockResource = "GONES_StockAudit";

        /// <summary>How long an audit waits for another audit to finish before failing (ms).</summary>
        private const int StockLockTimeoutMs = 30000;

        /// <summary>
        /// Open a transaction for any action that moves stock (audit / un-audit).
        ///
        /// Two layers, because either alone is not enough:
        ///   1. EF transaction  - makes the state flip, the ledger stamps and the cache
        ///                        re-derivation all-or-nothing (previously SaveChanges was called
        ///                        3-5 times per action: a crash between two of them left the
        ///                        caches contradicting the ledger).
        ///   2. Advisory lock  - serialises the whole action. Row locks taken by the ledger
        ///                        pre-lock queries cannot block a concurrent INSERT of a new
        ///                        bill, so a check-then-act race (two audits both reading
        ///                        "enough stock") is still possible under READ COMMITTED.
        ///                        The application lock closes that window.
        /// Throws InvalidOperationException when the lock cannot be granted in time.
        ///
        /// CONTRACT — caller MUST use this transaction:
        ///   using (var tx = _stock.BeginStockTransaction(_db))
        ///   {
        ///       // any number of StockService writes here, plus EF SaveChanges();
        ///       // ALL of them enlist in `tx` (EF auto-enlist) so the audit/in-out-stock
        ///       // and cache re-derivation land atomically.
        ///       tx.Commit();
        ///   }
        /// StockService writes do NOT call SaveChanges themselves except for the explicit
        /// `db.SaveChanges()` inside ApplyInStock / ApplyOutStock / RevertInStock / RevertOutStock
        /// (which is also enlisted in the caller tx). Re-derive (RecalcBatch/RecalcItem/RecalcAffected)
        /// is called AFTER the lock-bearing SaveChanges but still inside the transaction.
        /// </summary>
        public IDbContextTransaction BeginStockTransaction(GonesPgDbContext db)
        {
            var tx = db.Database.BeginTransaction();
            try
            {
                // pg_try_advisory_xact_lock returns true (granted) / false (already held by another
                // transaction in this or another session). It is transaction-scoped, so it is released
                // automatically on COMMIT or ROLLBACK -- the same lifetime SQL Server's
                // sp_getapplock @LockOwner='Transaction' gave us. There is no timeout variant in PG,
                // so a contended lock fails immediately (rc < 0 -> caller surfaces "库存操作忙" and
                // retries), which preserves the original contract. The key is derived deterministically
                // from the resource name so every caller locks the same slot.
                long lockKey = 0L;
                foreach (var ch in StockLockResource) lockKey = (lockKey * 31 + ch) & 0x7fffffffffffffffL;
                int rc = db.Database.SqlQueryRaw<int>(
                    // ⚠ 两坑（2026-09-13 探针实证）：① 末尾禁带分号——EF 标量 SqlQueryRaw 会把本 SQL
                    //   包进子查询（SELECT s."Value" FROM ( ... ) AS s），分号落入子查询 → 42601；
                    //   ② 内层输出列必须别名 AS "Value"，否则 42703 字段 s.Value 不存在。
                    "SELECT CASE WHEN pg_try_advisory_xact_lock(@p0) THEN 0 ELSE -1 END AS \"Value\"",
                    lockKey).First();

                if (rc < 0)
                {
                    tx.Rollback();
                    tx.Dispose();
                    throw new InvalidOperationException(
                        "\u5e93\u5b58\u64cd\u4f5c\u5fd9\uff08\u5176\u4ed6\u7528\u6237\u6b63\u5728\u5ba1\u6838\uff09\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5\u3002");   // 库存操作忙（其他用户正在审核），请稍后重试。
                }
                return tx;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already dead - nothing to undo */ }
                tx.Dispose();
                throw;
            }
        }

        // ------------------------------------------------------------------ business actions

        /// <summary>
        /// Audit of an in-stock bill: register one batch per entry (master data only), stamp
        /// FBatchNo / FBatchNoID / FROB on the bill rows, then re-derive the caches.
        /// Returns false (with error) when a 生产入库单 (FBillType=1) row's item has no 出厂单价
        /// (ccdj) — 在产品入成品仓按出厂单价计价，金额冻结前提是审核时点 ccdj 必须存在。
        /// </summary>
        public bool ApplyInStock(GonesPgDbContext db, t_PMS_StockBill header,
                                 List<t_PMS_StockBillEntry> entries, string userName,
                                 out string error)
        {
            error = null;
            if (header == null || entries == null || entries.Count == 0) return true;

            lock (BatchLock)
            {
                int nextInterId = NextBatchInterId(db);
                // running daily sequence per prefix so two entries of the same class in one
                // bill get distinct batch numbers even before the first one is persisted.
                var usedSeq = new Dictionary<string, int>();

                // 出厂价字典（t_ERP_ITEM.FactoryPrice），供生产入库单（FBillType=1）生成批号时
                // 默认作为批次单价使用（生产入库单不填采购含税价，FAfterTaxPrice 为空）。
                var itemIds = entries.Where(e => e.FItemID.HasValue)
                                     .Select(e => e.FItemID.Value).Distinct().ToList();
                var outPriceByItem = db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID))
                                                 .ToDictionary(i => i.ID, i => (decimal?)i.FactoryPrice);

                // ── G1 前置校验：生产入库单（FB=1）在产品入成品仓按出厂单价计价 ──
                // 任一明细商品 ccdj 为空 ⇒ 盖章分支会被跳过 → FPrice/FAmount 留 NULL，
                // 违反「金额冻结」不变量。审核时点直接拒审并点名缺失商品，迫使先维护出厂价。
                if (header.FBillType == 1)
                {
                    var nullCcdjIds = outPriceByItem.Where(kv => kv.Value == null)
                                                   .Select(kv => kv.Key).ToList();
                    if (nullCcdjIds.Count > 0)
                    {
                        var names = db.t_ERP_ITEM.Where(i => nullCcdjIds.Contains(i.ID))
                                                .Select(i => i.ItemShortName ?? i.ItemCode ?? i.ID.ToString())
                                                .ToList();
                        error = "\u4ee5\u4e0b\u5546\u54c1\u672a\u8bbe\u7f6e\u51fa\u5382\u5355\u4ef7\uff08ccdj\uff09\uff0c\u65e0\u6cd5\u5ba1\u6838\u751f\u4ea7\u5165\u5e93\u5355\uff1a"
                              + string.Join("\u3001", names)
                              + "\uff1b\u8bf7\u5148\u5728\u5546\u54c1\u6863\u6848\u7ef4\u62a4\u51fa\u5382\u4ef7\u3002";   // 以下商品未设置出厂单价（ccdj），无法审核生产入库单：…；请先在商品档案维护出厂价。
                        return false;
                    }
                }

                foreach (var e in entries.OrderBy(x => x.FEntryID))
                {
                    if (!e.FItemID.HasValue) continue;

                    var lb = ItemClassLb(db, e.FItemID);
                    var date = header.FDate ?? DateTime.Today;
                    var prefix = date.ToString("yyyyMMdd") + ClassCodeOf(lb);
                    int seq = NextBatchSeq(db, prefix) + 1;   // first unused daily seq for this prefix
                    if (usedSeq.TryGetValue(prefix, out int local) && local >= seq) seq = local + 1;
                    usedSeq[prefix] = seq;
                    var batchNo = prefix + seq.ToString("000");
                    int batchInterId = nextInterId++;

                    // 1) batch master data - NO quantity here, it is derived below
                    // 批次单价：生产入库单（FBillType=1）默认用商品出厂价（t_ERP_ITEM.FactoryPrice）；
                    // 采购入库单（FBillType=0）沿用单据含税价（FAfterTaxPrice）。
                    decimal? batchPrice = e.FAfterTaxPrice;
                    if (header.FBillType == 1 && e.FItemID.HasValue
                        && outPriceByItem.ContainsKey(e.FItemID.Value))
                        batchPrice = outPriceByItem[e.FItemID.Value];

                    db.t_PMS_BatchNoStock.Add(new t_PMS_BatchNoStock
                    {
                        FInterID = batchInterId,
                        FItemID = e.FItemID,
                        FBatchNum = 0m,
                        FBatchNo = batchNo,
                        FBillType = header.FBillType,
                        FPrice = batchPrice,
                        FBegDate = header.FDate,
                        FEndDate = e.FEndDate,
                        FState = true,
                        FName = userName
                    });

                    // 2) stamp the ledger row - this is what actually moves the stock
                    e.FBatchNo = batchNo;
                    e.FBatchNoID = batchInterId;
                    e.FROB = DirIn;

                    // 2b) 明细单价/金额：生产入库单（FBillType=1）以出厂价 ccdj 落库，与上面的
                    //     批号价同源，并把价格冻结在审核时点——之后商品出厂价再变，历史单据的
                    //     金额也不会漂移。采购入库单（FB=0）的明细价由采购单写入，此处不覆盖。
                    if (header.FBillType == 1 && batchPrice.HasValue)
                    {
                        e.FPrice = batchPrice;
                        e.FAmount = RowQty(e) * batchPrice.Value;
                    }
                }

                // Mark the header audited BEFORE deriving the caches: the ledger aggregation
                // only counts FState""=true bills, so the just-audited rows must already be flagged
                // or they would be excluded and FStockNum would resolve to 0.
                header.FState = true;
                db.SaveChanges();
            }

            // 3) derive the caches from the ledger (outside the lock, nothing depends on Max+1 here)
            RecalcAffected(db, entries);
            return true;
        }

        /// <summary>
        /// Un-audit of an in-stock bill: refuse when the batch was already consumed, otherwise
        /// clear the batch stamp, drop the batch master row and re-derive the caches.
        /// No manual subtraction anywhere.
        /// </summary>
        public bool RevertInStock(GonesPgDbContext db, t_PMS_StockBill header,
                                  List<t_PMS_StockBillEntry> entries, out string error)
        {
            error = null;
            if (header == null || entries == null || entries.Count == 0) return true;

            var batchNos = new List<string>();
            var itemIds = new List<int>();
            var toDrop = new List<t_PMS_BatchNoStock>();

            // The occupancy check must run while the bill is still audited (FState""=true), otherwise
            // the derived balance would already exclude this bill and everything looks consumed.
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.FBatchNo)) continue;

                // derived balance must still equal what this bill put in
                decimal derived = BatchQty(db, e.FBatchNo, forUpdate: true);
                decimal put = RowQty(e);
                if (derived < put - 0.0001m)
                {
                    error = "\u6279\u6b21 " + e.FBatchNo
                          + " \u5df2\u88ab\u540e\u7eed\u5355\u636e\u5360\u7528\uff0c\u65e0\u6cd5\u53cd\u5ba1\u6838\uff01";  // 批次 ... 已被后续单据占用，无法反审核！
                    return false;
                }
            }

            // Now de-audit first, then wipe the stamps, then re-derive.
            header.FState = false;
            db.SaveChanges();

            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.FBatchNo)) continue;

                var b = db.t_PMS_BatchNoStock.FirstOrDefault(x => x.FBatchNo == e.FBatchNo);
                if (b != null) toDrop.Add(b);

                batchNos.Add(e.FBatchNo);
                if (e.FItemID.HasValue) itemIds.Add(e.FItemID.Value);

                e.FBatchNo = "";
                e.FBatchNoID = null;
            }

            db.SaveChanges();

            // Batch master rows were created by this bill, so they go away with it.
            // No AFTER DELETE trigger exists any more (tri_del_batchNoEntry was dropped
            // together with t_PMS_BatchNoStockEntry), so a plain remove is safe.
            if (toDrop.Count > 0)
            {
                foreach (var b in toDrop) db.t_PMS_BatchNoStock.Remove(b);
                db.SaveChanges();
            }

            foreach (var no in batchNos.Distinct()) RecalcBatch(db, no);
            foreach (var id in itemIds.Distinct()) RecalcItem(db, id);
            db.SaveChanges();

            return true;
        }

        /// <summary>
        /// Operator-facing label for an item, used inside error messages so the user reads a
        /// product name instead of a bare numeric ID. Same shape as the 盘亏 path:
        /// `【cpjc】`, degrading to `【商品 {id}】` when the master row has no short name.
        /// </summary>
        private static string ItemLabel(GonesPgDbContext db, int itemId)
        {
            var name = db.t_ERP_ITEM.Where(i => i.ID == itemId).Select(i => i.ItemShortName).FirstOrDefault();
            return "\u3010" + (string.IsNullOrEmpty(name) ? ("\u5546\u54c1 " + itemId) : name) + "\u3011";
        }

        /// <summary>
        /// FIFO allocation of available batch stock for one item. Walks batches oldest-first
        /// (FBegDate, then FInterID) and carves out `qty`, skipping any qty already reserved by a
        /// sibling row in the same bill (so two rows for the same item do not double-commit one
        /// batch). Throws InvalidOperationException if the ledger cannot cover the request.
        /// Returns (batchNo, batchInterId, allocatedQty) tuples that sum to `qty`.
        /// Uses BatchQtyMap (one GROUP BY query for all candidate batches) instead of a
        /// per-batch BatchQty() call -- the old loop did one ledger aggregation per candidate
        /// row and was O(N) round-trips for any bill with N batches of one item.
        /// </summary>
        public List<(string batchNo, int? batchId, decimal num)> AllocateFifo(
            GonesPgDbContext db, int itemId, decimal qty,
            Dictionary<string, decimal> reserved = null)
        {
            var result = new List<(string, int?, decimal)>();
            if (qty <= 0m) return result;

            // Both shortage branches below differ only in how much is still missing, so share one
            // formatter -- the operator-facing wording can then never drift between them.
            string Shortage(decimal missing) =>
                "\u5e93\u5b58\u4e0d\u8db3\uff1a" + ItemLabel(db, itemId)
                + "\u9700\u8981 " + qty.ToString("0.####")
                + "\uff0c\u4ecd\u7f3a " + missing.ToString("0.####");   // 库存不足：【商品名】需要 X，仍缺 Y

            // Reservations are accumulated locally and merged into the caller's dictionary only
            // once the whole allocation succeeds. A throw part-way through must not leave phantom
            // reservations behind -- a caller that skips the failed row and carries on (e.g. the
            // 领料申请单 import) would otherwise see that batch as already held and wrongly refuse
            // a sibling row for the same item.
            var pending = new Dictionary<string, decimal>(StringComparer.Ordinal);

            decimal HeldBy(string batchNo)
            {
                decimal held = 0m;
                if (reserved != null && reserved.TryGetValue(batchNo, out var h)) held += h;
                if (pending.TryGetValue(batchNo, out var p)) held += p;
                return held;
            }

            var batches = db.t_PMS_BatchNoStock
                .Where(b => b.FItemID == itemId && (b.FState ?? false))
                .OrderBy(b => b.FBegDate).ThenBy(b => b.FInterID)
                .ToList();
            if (batches.Count == 0)
            {
                throw new InvalidOperationException(Shortage(qty));
            }

            // One query for the whole batch set -- avoids N+1 BatchQty() round-trips.
            var qtyMap = BatchQtyMap(db, batches.Select(b => b.FBatchNo));

            decimal need = qty;
            foreach (var b in batches)
            {
                if (need <= 0.0001m) break;
                decimal avail = qtyMap.TryGetValue(b.FBatchNo, out var v) ? v : 0m;   // truth, not the cache
                avail -= HeldBy(b.FBatchNo);                  // already held by this bill (committed + pending)
                if (avail <= 0.0001m) continue;

                decimal take = (avail < need) ? avail : need;
                result.Add((b.FBatchNo, b.FInterID, take));
                pending[b.FBatchNo] = (pending.TryGetValue(b.FBatchNo, out var pv) ? pv : 0m) + take;
                need -= take;
            }

            if (need > 0.0001m)
            {
                throw new InvalidOperationException(Shortage(need));
            }

            // Success only: now the holds become visible to the caller.
            if (reserved != null)
            {
                foreach (var kv in pending)
                    reserved[kv.Key] = (reserved.TryGetValue(kv.Key, out var cur) ? cur : 0m) + kv.Value;
            }
            return result;
        }

        /// <summary>
        /// Audit of an OUT-stock bill (e.g. warehouse picking, FBillType=8). The batch master rows
        /// already exist (created by earlier in-stock bills), so this only STAMPS the selected batch
        /// on each entry, validates the remaining ledger covers the take, flips FState and re-derives
        /// the caches. No new batch master rows are created here.
        /// Returns false (with error) if any row's batch cannot cover its qty or is missing.
        /// </summary>
        public bool ApplyOutStock(GonesPgDbContext db, t_PMS_StockBill header,
                                  List<t_PMS_StockBillEntry> entries, out string error)
        {
            error = null;
            if (header == null || entries == null || entries.Count == 0)
            {
                if (header != null) header.FState = true;
                return true;
            }

            // Quantities already claimed by earlier rows of THIS bill. Without it two rows
            // pointing at the same batch would each be checked against the full balance and
            // together consume the same stock twice (bill is still un-audited here, so the
            // ledger does not contain its own rows yet).
            var reserved = new Dictionary<string, decimal>();

            foreach (var e in entries)
            {
                // Zero-qty rows (by-product/waste recorded for traceability, later returned to
                // stock via the warehouse return module) move no stock: skip batch/stock checks
                // and leave FROB untouched. The ledger SQL and RecalcAffected already exclude
                // empty-batch rows, so this is safe.
                if ((e.FNum ?? 0m) == 0m) continue;
                // Defense in depth: the UI validators reject negative out-qtys, but this is the
                // last gate before the ledger. A negative FNum here would sail through the
                // balance check (avail < negative is never true) and be stamped FROB=-1, which
                // the ledger SUM(FNum*FROB) turns into a CREDIT -- stock out of thin air.
                if ((e.FNum ?? 0m) < 0m)
                {
                    error = "\u51fa\u5e93\u6570\u91cf\u4e0d\u80fd\u4e3a\u8d1f\u6570\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";   // 出库数量不能为负数，无法审核。
                    return false;
                }
                if (!e.FItemID.HasValue || string.IsNullOrEmpty(e.FBatchNo))
                {
                    error = "\u51fa\u5e93\u660e\u7ec6\u7f3a\u5c11\u6279\u6b21\u6216\u5546\u54c1\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";   // 出库明细缺少批次或商品，无法审核。
                    return false;
                }
                decimal avail = BatchQty(db, e.FBatchNo, forUpdate: true);   // truth
                if (reserved.TryGetValue(e.FBatchNo, out decimal held)) avail -= held;
                decimal take = e.FNum ?? 0m;
                if (avail < take - 0.0001m)
                {
                    error = "\u6279\u6b21 " + e.FBatchNo + " \u73b0\u6709\u5e93\u5b58 " + avail.ToString("0.####")
                          + " \u4e0d\u8db3\u4ee5\u51fa\u5e93 " + take.ToString("0.####") + "\u3002";   // 批次 X 现有库存 Y 不足以出库 Z。
                    return false;
                }
                reserved[e.FBatchNo] = held + take;
                e.FROB = DirOut;   // -1: this is what actually moves the stock
            }

            header.FState = true;
            db.SaveChanges();
            RecalcAffected(db, entries);
            return true;
        }

        /// <summary>
        /// Un-audit of a RETURN bill (FBillType=3 warehouse return / 2 department return).
        /// A return moves stock IN, so un-auditing it TAKES stock back out. Without an
        /// occupancy check the derived balance can go negative when the returned qty was
        /// already consumed by a later audited picking bill -- negative stock then silently
        /// blocks every unrelated out-stock check. Mirrors the guard in RevertInStock.
        /// </summary>
        public bool RevertReturnStock(GonesPgDbContext db, t_PMS_StockBill header,
                                      List<t_PMS_StockBillEntry> entries, out string error)
        {
            error = null;
            if (header == null) return true;
            if (entries == null || entries.Count == 0)
            {
                header.FState = false;
                db.SaveChanges();
                return true;
            }

            // Must run while the bill is still audited (FState""=true), otherwise the derived
            // balance would already exclude this bill and everything looks consumed.
            foreach (var e in entries)
            {
                decimal put = RowQty(e);
                if (put <= 0m) continue;

                decimal derived = !string.IsNullOrEmpty(e.FBatchNo)
                    ? BatchQty(db, e.FBatchNo, forUpdate: true)
                    : (e.FItemID.HasValue ? ItemQty(db, e.FItemID.Value, forUpdate: true) : 0m);

                if (derived < put - 0.0001m)
                {
                    error = "\u9000\u6599\u6570\u91cf\u5df2\u88ab\u540e\u7eed\u5355\u636e\u5360\u7528\uff0c\u65e0\u6cd5\u53cd\u5ba1\u6838\uff01";   // 退料数量已被后续单据占用，无法反审核！
                    return false;
                }
            }

            header.FState = false;
            db.SaveChanges();
            RecalcAffected(db, entries);
            return true;
        }

        /// <summary>
        /// Un-audit of an OUT-stock bill: flip FState off and re-derive. No batch master rows are
        /// dropped (they belong to the in-stock bills that created the stock). The caller is
        /// responsible for reversing any upstream back-reference (e.g. t_PMS_BillUse.FCurrentUseNum).
        /// </summary>
        public void RevertOutStock(GonesPgDbContext db, t_PMS_StockBill header,
                                   List<t_PMS_StockBillEntry> entries)
        {
            if (header == null || entries == null) return;
            header.FState = false;
            db.SaveChanges();
            RecalcAffected(db, entries);
        }

        // ------------------------------------------------------------------ stock-check derivation

        /// <summary>
        /// Next t_PMS_StockBill.FInterID. The column is NOT an identity (legacy Max+1), so the
        /// CALLER MUST hold <see cref=BillNoLock/> across read -> insert -> SaveChanges.
        /// </summary>
        public int NextBillInterId(GonesPgDbContext db)
        {
            return (db.t_PMS_StockBill.Max(h => (int?)h.FInterID) ?? 0) + 1;
        }

        /// <summary>
        /// Next bill no for one FBillType: prefix + 8 digits (PDYYD00000001), the same shape
        /// every other module uses. Rows that do not match the prefix/length are skipped so a
        /// dirty value cannot poison the sequence. Same <see cref=BillNoLock/> contract as
        /// <see cref=NextBillInterId/>.
        /// </summary>
        public string NextBillNoFor(GonesPgDbContext db, int billType, string prefix)
        {
            int maxNo = 0;
            foreach (var bn in db.t_PMS_StockBill.Where(h => h.FBillType == billType).Select(h => h.FBillNo))
            {
                if (string.IsNullOrEmpty(bn) || bn.Length != prefix.Length + 8 || !bn.StartsWith(prefix)) continue;
                if (int.TryParse(bn.Substring(prefix.Length), out int no) && no > maxNo) maxNo = no;
            }
            return prefix + (maxNo + 1).ToString("00000000");
        }

        private t_PMS_StockBill NewDerivedHeader(GonesPgDbContext db, t_PMS_StockBill sheet, int billType,
                                                 string prefix, string typeEx, short rob, int interId)
        {
            return new t_PMS_StockBill
            {
                FInterID = interId,
                FBillNo = NextBillNoFor(db, billType, prefix),
                FBillType = billType,
                FBillTypeEx = typeEx,
                FDate = sheet.FDate ?? DateTime.Today,
                FDeptID = sheet.FDeptID,
                FWorkShopID = null,
                FGroupID = null,
                FManagerID = sheet.FManagerID,
                FRemark = "\u6e90\u76d8\u70b9\u5355 " + sheet.FBillNo,   // 源盘点单 CKPDD00000001
                FCreaterID = sheet.FCreaterID,
                FROB = rob,
                FState = false,
                FSourceInterID = sheet.FInterID,
            };
        }

        private static t_PMS_StockBillEntry NewDerivedRow(int interId, int itemId, decimal qty,
                                                          t_PMS_StockBillEntry src, short rob)
        {
            return new t_PMS_StockBillEntry
            {
                FInterID = interId,
                FStepID = 0,                 // NOT NULL
                FBomId = 0,                  // NOT NULL
                FProduceNo = "",
                FProduceID = 0,
                FItemID = itemId,
                FPlanNum = 0m,               // only the SHEET uses FPlanNum for the counted qty
                FNum = qty,
                FAfterTaxPrice = src?.FAfterTaxPrice,
                FNote = "",
                FBatchNo = "",               // stamped by ApplyInStock (盘盈) / AllocateFifo (盘亏)
                FBatchNoID = null,
                FBillUseEntryID = 0,
                FBillUseNo = "",
                FROB = rob,
            };
        }

        /// <summary>
        /// Audit of a stock-check SHEET: compares every item's physical count against the ledger
        /// truth and derives up to two REAL documents inside the caller's transaction:
        ///
        ///     delta = counted - book
        ///       delta &gt; 0  ->  盘盈单 (FBillType=7 / PDYYD): one brand-new batch per item, ApplyInStock
        ///       delta &lt; 0  ->  盘亏单 (FBillType=10 / PDYKD): FIFO-carved off existing batches,
        ///                        AllocateFifo + ApplyOutStock
        ///
        /// The sheet row never moves stock itself (FNum = 0), which makes the whole thing
        /// IDEMPOTENT: re-counting the same number yields delta 0 and derives nothing -- the old
        /// "surplus stacks up, loss is never written off" behaviour is gone.
        ///
        /// Both derived headers carry FSourceInterID = sheet.FInterID so
        /// <see cref=DeriveStockCheckUnAudit/> can roll them back.
        /// CALLER MUST hold <see cref=BillNoLock/> (ids / bill nos are Max+1) AND a stock
        /// transaction. Returns false with a message when a loss exceeds the batch ledger.
        /// </summary>
        public bool DeriveStockCheckAudit(GonesPgDbContext db, t_PMS_StockBill sheet,
                                          List<t_PMS_StockBillEntry> sheetRows, string userName,
                                          out string error)
        {
            error = null;
            if (sheet == null) return true;

            // 1) physical count per item. The counted qty lives in FPlanNum; FNum is always 0.
            var rows = (sheetRows ?? new List<t_PMS_StockBillEntry>())
                .Where(e => e.FItemID.HasValue && e.FItemID.Value > 0)
                .ToList();

            var surplus = new List<(int itemId, decimal qty, t_PMS_StockBillEntry src)>();
            var loss = new List<(int itemId, decimal qty, t_PMS_StockBillEntry src)>();

            foreach (var g in rows.GroupBy(e => e.FItemID.Value))
            {
                decimal counted = g.Sum(e => e.FPlanNum ?? 0m);
                decimal book = ItemQty(db, g.Key, forUpdate: true);   // truth, not the cache
                decimal delta = counted - book;
                var src = g.First();                                  // price / note template
                if (delta > 0.0001m) surplus.Add((g.Key, delta, src));
                else if (delta < -0.0001m) loss.Add((g.Key, -delta, src));
            }

            int nextId = NextBillInterId(db);

            // ---- 盘盈 (FROB=+1, new batches)
            if (surplus.Count > 0)
            {
                var hdr = NewDerivedHeader(db, sheet, StockSurplusBillType, StockSurplusBillNoPrefix,
                                           StockSurplusBillTypeEx, DirIn, nextId++);
                db.t_PMS_StockBill.Add(hdr);

                var newRows = surplus.Select(s => NewDerivedRow(hdr.FInterID, s.itemId, s.qty, s.src, DirIn))
                                     .ToList();
                foreach (var r in newRows) db.t_PMS_StockBillEntry.Add(r);
                db.SaveChanges();

                if (!ApplyInStock(db, hdr, newRows, userName, out string inErr))
                {
                    error = inErr;     // 盘盈走 FB=7，G1 仅校验 FB=1，正常不会在此触发
                    return false;
                }   // sets FState, stamps batch, re-derives
            }

            // ---- 盘亏 (FROB=-1, carved off existing batches oldest-first)
            if (loss.Count > 0)
            {
                var hdr = NewDerivedHeader(db, sheet, StockLossBillType, StockLossBillNoPrefix,
                                           StockLossBillTypeEx, DirOut, nextId++);
                db.t_PMS_StockBill.Add(hdr);

                var reserved = new Dictionary<string, decimal>(StringComparer.Ordinal);
                var newRows = new List<t_PMS_StockBillEntry>();
                foreach (var l in loss)
                {
                    List<(string batchNo, int? batchId, decimal num)> alloc;
                    try
                    {
                        alloc = AllocateFifo(db, l.itemId, l.qty, reserved);
                    }
                    catch (InvalidOperationException)
                    {
                        error = ItemLabel(db, l.itemId)
                              + "\u76d8\u70b9\u4e8f\u635f\u6570\u91cf\u8d85\u8fc7\u8be5\u5546\u54c1\u73b0\u6709\u6279\u6b21\u5e93\u5b58\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";
                        // 【xx】盘点亏损数量超过该商品现有批次库存，无法审核。
                        return false;
                    }

                    foreach (var a in alloc)
                    {
                        var row = NewDerivedRow(hdr.FInterID, l.itemId, a.num, l.src, DirOut);
                        row.FBatchNo = a.batchNo;
                        row.FBatchNoID = a.batchId;
                        newRows.Add(row);
                    }
                }

                foreach (var r in newRows) db.t_PMS_StockBillEntry.Add(r);
                db.SaveChanges();

                if (!ApplyOutStock(db, hdr, newRows, out string lossErr))
                {
                    error = lossErr;
                    return false;
                }
            }

            sheet.FState = true;
            db.SaveChanges();
            return true;
        }

        /// <summary>
        /// Un-audit of a stock-check sheet: rolls the derived 盘盈 / 盘亏 bills back FIRST (they
        /// are located by FSourceInterID), deletes them, then flips the sheet back to draft.
        /// Refuses when the derived 盘盈 batch was already consumed by a later audited bill
        /// (RevertInStock owns that guard). CALLER MUST hold <see cref=BillNoLock/> and a stock
        /// transaction.
        /// </summary>
        public bool DeriveStockCheckUnAudit(GonesPgDbContext db, t_PMS_StockBill sheet, out string error)
        {
            error = null;
            if (sheet == null) return true;

            var derived = db.t_PMS_StockBill
                .Where(h => h.FSourceInterID == sheet.FInterID
                            && (h.FBillType == StockSurplusBillType || h.FBillType == StockLossBillType))
                .OrderBy(h => h.FInterID)
                .ToList();

            foreach (var d in derived)
            {
                var rows = db.t_PMS_StockBillEntry.Where(e => e.FInterID == d.FInterID).ToList();

                if (d.FBillType == StockSurplusBillType)
                {
                    if (!RevertInStock(db, d, rows, out string revertErr))
                    {
                        error = revertErr;
                        return false;
                    }
                }
                else
                {
                    RevertOutStock(db, d, rows);   // flips FState + re-derives; no master rows to drop
                }

                db.t_PMS_StockBillEntry.RemoveRange(rows);
                db.t_PMS_StockBill.Remove(d);
                db.SaveChanges();
            }

            sheet.FState = false;
            db.SaveChanges();
            return true;
        }

        /// <summary>
        /// Audit of a warehouse RETURN bill (FBillType=3, FROB=+1): the batches were created by
        /// earlier in-stock bills (the return re-uses the picking bill's batch nos), so this only
        /// stamps DirIn on each row, flips FState and re-derives the caches. No new batch master
        /// rows are created, none are dropped on un-audit (RevertOutStock handles that side).
        /// Qty semantics: FNum = normal return, FNumExt1/2/3 = vendor / human / other reason;
        /// all stored positive, direction comes from FROB. The ledger SQL sums FNum + all Exts.
        /// Returns false (with error) if a row lacks item or batch.
        /// </summary>
        public bool StampReturnStock(GonesPgDbContext db, t_PMS_StockBill header,
                                     List<t_PMS_StockBillEntry> entries, out string error)
        {
            error = null;
            if (header == null || entries == null || entries.Count == 0)
            {
                if (header != null) header.FState = true;
                return true;
            }

            foreach (var e in entries)
            {
                // waste rows carry no batch (source pick qty = 0); they only bump the
                // item-level ledger (batch-level SQL excludes empty batch nos).
                if (!e.FItemID.HasValue)
                {
                    error = "\u9000\u6599\u660e\u7ec6\u7f3a\u5c11\u5546\u54c1\uff0c\u65e0\u6cd5\u5ba1\u6838\u3002";   // 退料明细缺少商品，无法审核。
                    return false;
                }
                e.FROB = DirIn;   // +1: return moves stock back in
            }

            header.FState = true;
            db.SaveChanges();
            RecalcAffected(db, entries);
            return true;
        }
    }
}
