using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 批次库存管理页（旧 WinForms Stock.FrmBatchStockManager / FrmBatchStockEdit）。
    /// 旧系统：商品库存列表 + 双击批次编辑(改日期) + 同步库存(重算) + 批量生成批号。
    /// 新「单据即账本」模型下：t_PMS_BatchNoStock 仅存单据主数据 + FBatchNum 可重算缓存，
    /// 真实批次余额由 t_PMS_StockBill/Entry 按 FROB 聚合推导（StockService.BatchQty）。
    /// 因此本控制器只读：查询批次余额、对比「缓存」与「账本真实量」、管理员可对账重算
    /// （RecalcAll）、双击批次看流水追溯。任何数量调整都须经由出入库单据，本页不手工改数。
    /// 菜单：t_ERP_Menu.ID=1316「生产批次管理」(ClassName=Stock.FrmBatchStockManager) → /BatchStock。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 标准 MVC 表单重显，跳过 Furion 400 短路
    public class BatchStockController : Controller, IMenuGuarded
    {
        // t_ERP_Menu.ID=1316「生产批次管理」(Stock.FrmBatchStockManager)

        private readonly GonesPgDbContext _db;
        private readonly StockService _stock;
        private readonly MenuService _menu;
        public BatchStockController(GonesPgDbContext db, StockService stock, MenuService menu)
        {
            _db = db;
            _stock = stock;
            _menu = menu;
        }

        private bool IsAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        /// <summary>
        /// 入口闸门：admin 旁路恒 true（行为逐字节不变）；非 admin 须由其角色的
        /// t_ERP_RoleMenu 授权覆盖本模块（1316），否则回首页 —— 与 7 个单据模块同一范式。
        /// 注：本页是纯查询/对账页，按既有裁定【允许跨仓查询】，故只做入口闸门、不收窄仓库维度。
        /// </summary>

        // ------------------------------------------------------------ list (read-only query)

        // GET /BatchStock?batchNo=&keyword=&classLb=&onlyPositive=&page=&pageSize=
        public IActionResult Index(string batchNo, string keyword, string classLb, bool? onlyPositive,
            int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);
            batchNo = batchNo?.Trim();
            keyword = keyword?.Trim();
            classLb = classLb?.Trim();

            // 商品筛选（编号/名称/规格/拼音码）与分类筛选
            var itemQ = _db.t_ERP_ITEM.AsQueryable();
            if (!string.IsNullOrEmpty(keyword))
            {
                itemQ = itemQ.Where(i => (i.ItemCode != null && i.ItemCode.Contains(keyword))
                                      || (i.ItemShortName != null && i.ItemShortName.Contains(keyword))
                                      || (i.ItemSpec != null && i.ItemSpec.Contains(keyword))
                                      || (i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(keyword)));
            }
            // 按分类名（lb）筛选：t_ERP_ITEMCLASS 是树形结构，同名 lb 会在多个节点出现，
            // 用 Any 子查询把同名分类节点下的商品一起命中（平铺 ID 下拉曾因此出现重复项）。
            if (!string.IsNullOrEmpty(classLb))
            {
                itemQ = itemQ.Where(i => _db.t_ERP_ITEMCLASS
                    .Any(c => c.ID == i.CategoryId && c.TopClassName == classLb));
            }

            // .Any subquery instead of itemIds.Contains: itemQ can match the ENTIRE item
            // table, and a plain IN-list would blow past SQL Server's 2100-parameter limit.
            var q = _db.t_PMS_BatchNoStock.AsQueryable()
                     .Where(b => b.FItemID.HasValue && itemQ.Any(i => i.ID == b.FItemID.Value));
            if (!string.IsNullOrEmpty(batchNo)) q = q.Where(b => b.FBatchNo.Contains(batchNo));
            // ⚠ onlyPositive 按**缓存列**过滤（按账本真值过滤需全表聚合，正是下方 truthMap
            //   刻意避免的）。代价：若某批缓存=0 而账本>0（漂移），勾选本筛选会把它连页脚
            //   漂移计数一起藏掉。接受理由：漂移仅在绕过 RecalcBatch 的写入时出现，有
            //   tests/probe_batch_cache_consistency.js 守卫；默认视图（不勾）恒显示全部并高亮漂移。
            if (onlyPositive == true) q = q.Where(b => (b.FBatchNum ?? 0m) > 0.0001m);

            int total = q.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);

            var batches = q.OrderByDescending(b => b.FInterID)
                           .Skip((page - 1) * pageSize)
                           .Take(pageSize)
                           .ToList();

            var itemIdsPage = batches.Where(b => b.FItemID.HasValue)
                                     .Select(b => b.FItemID.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => itemIdsPage.Contains(i.ID))
                                     .ToDictionary(i => i.ID);
            var clsLbMap = _db.t_ERP_ITEMCLASS.ToDictionary(c => c.ID, c => c.TopClassName);
            // 只算「当页」批次的账本真实量：旧写法 BatchQtyAll 对整张 t_PMS_StockBillEntry
            // 全表 GROUP BY，而本页只显示 20 行，账本一长大就线性变慢。
            var truthMap = _stock.BatchQtyMap(_db, batches.Select(b => b.FBatchNo));

            string ClassLbOf(int? id) =>
                (id.HasValue && clsLbMap.ContainsKey(id.Value)) ? (clsLbMap[id.Value] ?? "") : "";

            var rows = new List<BatchStockListRow>();
            foreach (var b in batches)
            {
                var it = (b.FItemID.HasValue && items.ContainsKey(b.FItemID.Value))
                    ? items[b.FItemID.Value] : null;
                decimal truth = truthMap.TryGetValue(b.FBatchNo, out var tv) ? tv : 0m;
                rows.Add(new BatchStockListRow
                {
                    BatchNo = b.FBatchNo,
                    ItemId = b.FItemID,
                    Cpbm = it?.ItemCode ?? "",
                    Cpjc = it?.ItemShortName ?? "",
                    Cpgg = it?.ItemSpec ?? "",
                    Cplb = ClassLbOf(it?.CategoryId),
                    UnitName = it?.BaseUnit ?? "",
                    CacheQty = b.FBatchNum ?? 0m,
                    TruthQty = truth,
                    Price = b.FPrice,
                    BegDate = b.FBegDate,
                    State = b.FState ?? false,
                    BillType = b.FBillType,
                    // 批次主档无 FBillTypeEx 字段，只能走 switch 兜底。
                    BillTypeEx = BillTypeExName(b.FBillType, null),
                });
            }

            // 汇总：当前筛选条件下缓存合计 / 账本真实合计 / 不一致批次数。
            // 三项全部在服务端算——缓存合计为单条 SUM；漂移交给 StockService.BatchDrift
            // （账本 GROUP BY 在 SQL 内 left join 批次主档，每块只回一行）。旧写法 q.Select(...).ToList()
            // 会把筛选命中的每一个批次都拉回内存，命中数一上来内存与响应时间双爆。
            decimal sumCache = q.Sum(b => (decimal?)(b.FBatchNum ?? 0m)) ?? 0m;
            var matchedNos = q.Select(b => b.FBatchNo).ToList();
            var drift = _stock.BatchDrift(_db, matchedNos);
            ViewBag.Summary = new BatchStockSummary
            {
                TotalCache = sumCache,
                TotalTruth = drift.TotalTruth,
                DriftCount = drift.DriftCount,
            };

            // 筛选回填 + 分页
            ViewBag.Filter = new Dictionary<string, string>
            {
                ["batchNo"] = batchNo ?? "",
                ["keyword"] = keyword ?? "",
                ["classLb"] = classLb ?? "",
                ["onlyPositive"] = (onlyPositive == true) ? "1" : "",
            };
            // 下拉候选 = 去重后的分类名（树形同名 lb 只出现一次；排除树根「全部」与空名）。
            ViewBag.ClassOptions = _db.t_ERP_ITEMCLASS
                .Select(c => c.TopClassName)
                .Distinct()
                .ToList()
                .Where(lb => !string.IsNullOrEmpty(lb) && lb != "\u5168\u90e8")
                .OrderBy(lb => lb)
                .ToList();
            ViewBag.IsAdmin = IsAdmin;

            var extra = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(batchNo)) extra["batchNo"] = batchNo;
            if (!string.IsNullOrEmpty(keyword)) extra["keyword"] = keyword;
            if (!string.IsNullOrEmpty(classLb)) extra["classLb"] = classLb;
            if (onlyPositive == true) extra["onlyPositive"] = "1";
            ViewBag.Pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index",
                ExtraValues = extra.Count > 0 ? extra : null,
            };

            return View(rows);
        }

        // ------------------------------------------------------------ reconcile (admin only)

        // POST /BatchStock/Recalc
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Recalc()
        {
            // RecalcAll 是全局（跨全部仓库）的修复性运维操作，admin 专属（服务端强制，
            // 不只靠列表页灰按钮）：可被高频滥用造成全表 UPDATE 压力。
            if (!User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1"))
            {
                TempData["Error"] = "\u53ea\u6709\u7ba1\u7406\u5458\u624d\u80fd\u6267\u884c\u5168\u5e93\u5bf9\u8d26\u91cd\u7b97\u3002";   // 只有管理员才能执行全库对账重算。
                return RedirectToAction(nameof(Index));
            }

            int drift = _stock.RecalcAll(_db);
            TempData["Success"] = drift > 0
                ? "\u5bf9\u8d26\u91cd\u7b97\u5b8c\u6210\uff0c\u5171\u4fee\u6b63 " + drift + " \u5904\u7f13\u5b58\u4e0e\u5355\u636e\u4e0d\u4e00\u81f4\u3002"   // 对账重算完成，共修正 N 处缓存与单据不一致。
                : "\u5bf9\u8d26\u91cd\u7b97\u5b8c\u6210\uff0c\u7f13\u5b58\u4e0e\u5355\u636e\u8d26\u672c\u5b8c\u5168\u4e00\u81f4\u3002";   // 对账重算完成，缓存与单据账本完全一致。
            return RedirectToAction(nameof(Index));
        }

        // ------------------------------------------------------------ trace (read-only drill-down)

        // GET /BatchStock/Trace?batchNo=xxx  => 构成该批次余额的出入库单据行
        [HttpGet]
        public IActionResult Trace(string batchNo)
        {

            if (string.IsNullOrEmpty(batchNo)) return Json(new List<object>());

            var raw = (from e in _db.t_PMS_StockBillEntry
                       join h in _db.t_PMS_StockBill on e.FInterID equals h.FInterID
                       where e.FBatchNo == batchNo
                       orderby h.FDate, e.FInterID, e.FEntryID
                       select new
                       {
                           h.FBillNo,
                           h.FDate,
                           h.FBillType,
                           BillTypeEx = h.FBillTypeEx,
                           e.FROB,
                           // Ledger quantity is FNum + the three reason-split columns, not
                           // FNum alone: warehouse returns (FBillType 3) store 厂家/人为/其它
                           // 原因 in the Ext columns, so summing FNum only would under-report
                           // every return and make the trace total disagree with the balance.
                           e.FNum,
                           e.FNumExt1,
                           e.FNumExt2,
                           e.FNumExt3,
                           e.FNote,
                           ItemId = e.FItemID,
                       }).ToList();

            var ids = raw.Where(x => x.ItemId != null).Select(x => x.ItemId.Value).Distinct().ToList();
            var items = _db.t_ERP_ITEM.Where(i => ids.Contains(i.ID)).ToDictionary(i => i.ID);

            var list = raw.Select(x =>
            {
                var it = (x.ItemId != null && items.ContainsKey(x.ItemId.Value))
                    ? items[x.ItemId.Value] : null;
                return new BatchTraceRow
                {
                    BillNo = x.FBillNo,
                    Date = x.FDate,
                    // 优先用表头 FBillTypeEx（x.BillTypeEx），缺失时才走 switch 兜底。
                    BillTypeEx = BillTypeExName(x.FBillType, x.BillTypeEx),
                    RobEx = (x.FROB == -1) ? "\u51fa" : "\u5165",   // 出 / 入
                    Num = (x.FNum ?? 0m) + (x.FNumExt1 ?? 0m) + (x.FNumExt2 ?? 0m) + (x.FNumExt3 ?? 0m),
                    ItemCode = it?.ItemCode ?? "",
                    ItemName = it?.ItemShortName ?? "",
                    Note = x.FNote ?? "",
                };
            }).ToList();

            return Json(list);
        }

        // ------------------------------------------------------------ helpers

        /// <summary>单据类型名（旧 FrmBatchStockEdit 的 FBillType 文案）。</summary>
        /// <summary>
        /// 单据类型文案：优先取表头 FBillTypeEx；为空（历史批次 / 未回填）再落 switch 兜底。
        /// </summary>
        private static string BillTypeExName(int? t, string billTypeEx)
        {
            if (!string.IsNullOrEmpty(billTypeEx))
                return billTypeEx;
            switch (t)
            {
                case 0: return "\u91c7\u8d2d\u5165\u5e93";        // 采购入库
                case 1: return "\u751f\u4ea7\u5165\u5e93";        // 生产入库
                case 2: return "\u90e8\u95e8\u9000\u6599";        // 部门退料
                case 3: return "\u4ed3\u5e93\u9000\u6599";        // 仓库退料
                // 旧窗体文案为「产品盘点」；但 FB=4 是 CKPDD 仓库盘点单（StockService.StockCheckBillType），
                // 且新旧两库的批次主档均无 FB=4 批次 → 按写库口径统一为「仓库盘点」。
                case StockService.StockCheckBillType: return "\u4ed3\u5e93\u76d8\u70b9";   // 仓库盘点
                case 5: return "\u4ea7\u54c1\u62a5\u635f";        // 产品报损
                case 6: return "\u6362\u8d27\u5165\u5e93";        // 换货入库
                case 8: return "\u4ed3\u5e93\u9886\u6599";        // 仓库领料
                case 9: return "\u90e8\u95e8\u9886\u6599";        // 部门领料
                case StockService.StockSurplusBillType: return StockService.StockSurplusBillTypeEx;   // 盘点盈余
                case StockService.StockLossBillType: return StockService.StockLossBillTypeEx;         // 盘点亏损
                default: return (t.HasValue ? t.Value.ToString() : "");
            }
        }
    }
}
