using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;

namespace GONES.Web.Services
{
    /// <summary>
    /// 仓库管理 9 个单据列表页共用的导出行模型 + 构建器。
    /// 与列表页同筛选、同仓库数据权限闸门（FilterBills / FilterEntries / BillsNotFullyInScope）。
    /// 各控制器 Export action 只需把 BuildRows 的结果按本页列做映射即可，避免 8 份重复的拼行代码。
    /// </summary>
    public class ExportBillRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        public string DeptName { get; set; }       // 部门（h.FDeptID）
        public string GroupName { get; set; }       // 班组（领料/退料单 h.FGroupID）
        public string WorkShopName { get; set; }    // 车间（生产入库/部门单据 h.FWorkShopID）
        public string WarehouseName { get; set; }   // 盘点仓库（盘点单 h.FWorkShopID）
        public string ManagerName { get; set; }     // 入库/领用/盘点人
        public string BuyingUnit { get; set; }       // 供货单位（表头）
        public int EntryId { get; set; }
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }
        public string Cplb { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string UnitName { get; set; }
        public decimal? Qty { get; set; }            // 通用数量 FNum
        public decimal? Num { get; set; }            // 退料单数量 FNum
        public decimal? NumExt1 { get; set; }        // 退料：厂家原因
        public decimal? NumExt2 { get; set; }        // 退料：人为原因
        public decimal? NumExt3 { get; set; }        // 退料：其它原因
        public decimal RowTotal { get; set; }        // 退料合计
        public decimal? PlanNum { get; set; }        // 计划数量 FPlanNum（生产入库）
        public string ProduceNo { get; set; }        // 生产单号 FProduceNo
        public string StepName { get; set; }         // 生产步骤 FStepID -> t_PMS_Step.FName
        public decimal? Price { get; set; }          // 单价 = FPrice ?? 商品出厂价 ccdj
        public decimal? Amount { get; set; }         // 金额（落库值 FAmount）
        public decimal? BookStock { get; set; }      // 账面库存 t_ERP_ITEM.StockQuantity（盘点单）
        public decimal? CheckQty { get; set; }       // 实盘数量 FPlanNum（盘点单）
        public decimal? CheckPrice { get; set; }     // 盘点单价 FAfterTaxPrice（盘点单）
        public string TaxRate { get; set; }
        public string Note { get; set; }
        public string FromBillUseNo { get; set; }    // 来源单号 FBillUseNo（领料申请/退料单）
        public bool CanModify { get; set; }
    }

    /// <summary>
    /// 仓库管理单据导出构建器：按 billType 过滤 + 仓库闸门，加载表头/明细/商品/部门/人员/步骤，
    /// 展开成 ExportBillRow 列表（不翻页，导出全部命中行）。
    /// <paramref name="pym"/> = 供货单位模糊匹配（采购入库页用）；
    /// <paramref name="pymItemKeyword"/> = 商品拼音码命中单据（ExchangeIn 页用，与其列表页同口径）。
    /// </summary>
    public static class StockBillExport
    {
        public static List<ExportBillRow> BuildRows(GonesPgDbContext db, DepotScope depot, int billType,
            DateTime? dateFrom, DateTime? dateTo, string billNo, string pym, int? state,
            string pymItemKeyword = null)
        {
            var hq = db.t_PMS_StockBill.Where(h => h.FBillType == billType);
            // 数据闸门①-a（行级读）：只保留"至少有一行明细属于本仓库"的单据。
            if (depot.IsRestricted) hq = depot.FilterBills(hq);
            // hoist .Date out of the lambdas：EF 不能翻译 Nullable<DateTime>.Date
            var dFrom = dateFrom.HasValue ? dateFrom.Value.Date : (DateTime?)null;
            var dTo = dateTo.HasValue ? dateTo.Value.Date.AddDays(1) : (DateTime?)null;
            if (dFrom.HasValue) hq = hq.Where(h => h.FDate >= dFrom.Value);
            if (dTo.HasValue) hq = hq.Where(h => h.FDate < dTo.Value);
            if (!string.IsNullOrEmpty(billNo)) hq = hq.Where(h => h.FBillNo.Contains(billNo));
            if (!string.IsNullOrEmpty(pym)) hq = hq.Where(h => h.FBuyingUnit.Contains(pym));
            if (state.HasValue && state.Value >= 0) hq = hq.Where(h => (h.FState ?? false) == (state.Value == 1));
            // 商品拼音码：只保留"单据里含有 cppym 命中该关键字的商品"的单据。
            // 口径逐条对齐 ExchangeInController.Index（列表页同款），否则该页筛选后导出会变成全量。
            if (!string.IsNullOrEmpty(pymItemKeyword))
            {
                var pymItemIds = db.t_ERP_ITEM
                    .Where(i => i.ItemPinyinCode != null && i.ItemPinyinCode.Contains(pymItemKeyword))
                    .Select(i => i.ID)
                    .ToList();
                var hitInterIds = db.t_PMS_StockBillEntry
                    .Where(e => e.FItemID != null && pymItemIds.Contains(e.FItemID.Value))
                    .Select(e => e.FInterID)
                    .Distinct()
                    .ToList();
                hq = hq.Where(h => hitInterIds.Contains(h.FInterID));
            }

            // 不翻页：导出全部命中表头
            var headers = hq.OrderByDescending(h => h.FInterID).ToList();
            var headerIds = headers.Select(h => h.FInterID).ToList();

            // 数据闸门①-c（明细行收窄）：只导出本仓库的明细行。
            var entryQ = db.t_PMS_StockBillEntry.Where(e => headerIds.Contains(e.FInterID));
            if (depot.IsRestricted) entryQ = depot.FilterEntries(entryQ);
            var entries = entryQ
                .OrderBy(e => e.FInterID).ThenBy(e => e.FEntryID)
                .ToList();
            var entriesByBill = entries.GroupBy(e => e.FInterID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var itemIds = entries.Where(e => e.FItemID != null).Select(e => e.FItemID.Value).Distinct().ToList();
            var items = db.t_ERP_ITEM.Where(i => itemIds.Contains(i.ID)).ToDictionary(i => i.ID);
            var depts = db.t_ERP_Department.ToDictionary(d => d.ID, d => d.DEPName);
            var managerIds = headers.Where(h => h.FManagerID != null).Select(h => h.FManagerID.Value).Distinct().ToList();
            var workers = db.t_PMS_Worker.Where(w => managerIds.Contains(w.ID)).ToDictionary(w => w.ID, w => w.FName);
            var groupIds = headers.Where(h => h.FGroupID != null).Select(h => h.FGroupID.Value).Distinct().ToList();
            var groups = db.t_ERP_Department.Where(d => groupIds.Contains(d.ID)).ToDictionary(d => d.ID, d => d.DEPName);
            var stepIds = entries.Select(e => e.FStepID).Where(x => x > 0).Distinct().ToList();
            var stepMap = db.t_PMS_Step.Where(s => stepIds.Contains(s.FItemID)).ToDictionary(s => s.FItemID, s => s.FName);

            string DeptName(int? id) => (id != null && depts.ContainsKey(id.Value)) ? depts[id.Value] : "";
            string WorkerName(int? id) => (id != null && workers.ContainsKey(id.Value)) ? workers[id.Value] : "";
            string GroupName(int? id) => (id != null && groups.ContainsKey(id.Value)) ? groups[id.Value] : "";

            // 数据闸门③-b（按钮灰化，与 W1 硬拦同源）：导出也标注越权灰化集合。
            var lockedByDepot = depot.BillsNotFullyInScope(headerIds);

            var rows = new List<ExportBillRow>();
            foreach (var h in headers)
            {
                var bills = entriesByBill.ContainsKey(h.FInterID) ? entriesByBill[h.FInterID] : new List<t_PMS_StockBillEntry>();
                var first = true;
                if (bills.Count == 0)
                {
                    rows.Add(new ExportBillRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = true,
                        DeptName = DeptName(h.FDeptID), GroupName = GroupName(h.FGroupID),
                        WorkShopName = DeptName(h.FWorkShopID), WarehouseName = DeptName(h.FWorkShopID),
                        ManagerName = WorkerName(h.FManagerID), BuyingUnit = h.FBuyingUnit,
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                    });
                    continue;
                }
                foreach (var e in bills)
                {
                    t_ERP_ITEM it = null;
                    if (e.FItemID != null && items.ContainsKey(e.FItemID.Value)) it = items[e.FItemID.Value];
                    decimal? price = e.FPrice ?? it?.FactoryPrice;
                    rows.Add(new ExportBillRow
                    {
                        InterId = h.FInterID, BillNo = h.FBillNo, Date = h.FDate, State = h.FState ?? false,
                        FirstOfBill = first,
                        DeptName = DeptName(h.FDeptID), GroupName = GroupName(h.FGroupID),
                        WorkShopName = DeptName(h.FWorkShopID), WarehouseName = DeptName(h.FWorkShopID),
                        ManagerName = WorkerName(h.FManagerID), BuyingUnit = h.FBuyingUnit,
                        EntryId = e.FEntryID, BatchNo = e.FBatchNo,
                        Cpbm = it?.ItemCode ?? "", Cplb = it?.ProductCategory ?? "", Cpjc = it?.ItemShortName ?? "",
                        Cpgg = it?.ItemSpec ?? "", UnitName = it?.BaseUnit ?? "",
                        Qty = e.FNum, Num = e.FNum,
                        NumExt1 = e.FNumExt1, NumExt2 = e.FNumExt2, NumExt3 = e.FNumExt3,
                        RowTotal = (e.FNum ?? 0m) + (e.FNumExt1 ?? 0m) + (e.FNumExt2 ?? 0m) + (e.FNumExt3 ?? 0m),
                        PlanNum = e.FPlanNum, ProduceNo = e.FProduceNo ?? "",
                        StepName = (e.FStepID > 0 && stepMap.ContainsKey(e.FStepID)) ? stepMap[e.FStepID] : "",
                        Price = price, Amount = e.FAmount,
                        BookStock = it?.StockQuantity, CheckQty = e.FPlanNum, CheckPrice = e.FAfterTaxPrice,
                        TaxRate = e.FTaxRate, Note = e.FNote, FromBillUseNo = e.FBillUseNo ?? "",
                        CanModify = !lockedByDepot.Contains(h.FInterID),
                    });
                    first = false;
                }
            }
            return rows;
        }
    }
}
