using System;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;

namespace GONES.Web.Services
{
    /// <summary>
    /// 生产 MES 数据看板 —— 数据构建器（静态，供 DashboardController 与 HomeController 共用）。
    /// 一次算好所有 KPI / 图表序列 / 明细列表，首页与 /Dashboard/Data 刷新接口共用同一份口径。
    /// </summary>
    public static class DashboardData
    {
        // 入库方向 / 出库方向（与全站账本口径一致）
        public static readonly int[] InBillTypes = { 0, 1, 2, 3, 4, 6, 7 };
        public static readonly int[] OutBillTypes = { 5, 8, 9, 10 };

        /// <summary>库存预警阈值：FStockNum &lt;= 0 视为缺货，&lt;= 此值视为偏低。</summary>
        public const decimal LowStockThreshold = 500m;

        public static DashboardViewModel Build(GonesPgDbContext db)
        {
            var vm = new DashboardViewModel();
            var today = DateTime.Today;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var start30 = today.AddDays(-29);

            // —— 生产订单 KPI ——
            var orders = db.t_PMS_ProduceOrder.ToList();
            vm.PoTotal = orders.Count;
            vm.PoDraft = orders.Count(o => o.FStatus == 0);
            vm.PoInProgress = orders.Count(o => o.FStatus == 1 && (o.FOrderStatus ?? 0) < 2);
            vm.PoCompleted = orders.Count(o => o.FStatus == 1 && (o.FOrderStatus ?? 0) == 2);
            vm.PoThisMonth = orders.Count(o => o.FDate >= monthStart);

            // —— 今日生产入库量（FB=1, 已审, 今日）——
            vm.TodayOutput = db.t_PMS_StockBillEntry
                .Join(db.t_PMS_StockBill, e => e.FInterID, h => h.FInterID, (e, h) => new { e, h })
                .Where(x => x.h.FState == true && x.h.FBillType == 1 && x.h.FDate >= today && x.h.FDate < today.AddDays(1))
                .Sum(x => x.e.FNum ?? 0m);

            // —— 库存 KPI ——
            var items = db.t_ERP_ITEM.ToList();
            vm.StockKinds = items.Count(i => (i.StockQuantity ?? 0) > 0);
            vm.StockTotalQty = items.Sum(i => i.StockQuantity ?? 0);

            // —— 待处理要货（未审核且未作废）——
            vm.PendingApply = db.t_PMS_GoodsApply.Count(a => a.FCancellation == false && a.FStatus != 1);

            // —— 本月报损（FB=5, 已审, 本月）——
            var loss = db.t_PMS_StockBillEntry
                .Join(db.t_PMS_StockBill, e => e.FInterID, h => h.FInterID, (e, h) => new { e, h })
                .Where(x => x.h.FState == true && x.h.FBillType == 5 && x.h.FDate >= monthStart)
                .Select(x => new { x.e.FNum, x.e.FAmount })
                .ToList();
            vm.MonthLossQty = loss.Sum(x => x.FNum ?? 0m);
            vm.MonthLossAmount = loss.Sum(x => x.FAmount ?? 0m);

            // —— 车间 / 工序数（车间=部门表 IsDEP=2；t_PMS_WorkShop 为遗留空表）——
            vm.WorkshopCount = db.t_ERP_Department.Count(d => d.IsDEP == 2);
            vm.StepCount = db.t_PMS_Step.Count();

            // —— 图表 A：近30天出入库趋势 ——
            var trend = db.t_PMS_StockBillEntry
                .Join(db.t_PMS_StockBill, e => e.FInterID, h => h.FInterID, (e, h) => new { e, h })
                .Where(x => x.h.FState == true && x.h.FDate >= start30)
                .Select(x => new { Date = x.h.FDate, BillType = x.h.FBillType ?? 0, Num = x.e.FNum ?? 0m })
                .AsEnumerable()
                .Where(x => x.Date.HasValue)
                .GroupBy(x => x.Date.Value.Date)
                .ToDictionary(g => g.Key, g => new
                {
                    In = g.Where(x => InBillTypes.Contains(x.BillType)).Sum(x => x.Num),
                    Out = g.Where(x => OutBillTypes.Contains(x.BillType)).Sum(x => x.Num)
                });
            for (var d = start30.Date; d <= today; d = d.AddDays(1))
            {
                vm.TrendDates.Add(d.ToString("MM-dd"));
                var t = trend.ContainsKey(d) ? trend[d] : null;
                vm.TrendIn.Add(t?.In ?? 0);
                vm.TrendOut.Add(t?.Out ?? 0);
            }

            // 部门/车间名称字典（FWorkShopID 已按迁移口径指向 t_ERP_Department.ID；t_PMS_WorkShop 为遗留空表）
            var deptNames = db.t_ERP_Department.ToDictionary(d => d.ID, d => d.DEPName);

            // —— 图表 B：近30天各车间/部门入库量（全部入库类型：FB ∈ {0,1,2,3,4,6,7}）——
            vm.WorkshopOutput = db.t_PMS_StockBillEntry
                .Join(db.t_PMS_StockBill, e => e.FInterID, h => h.FInterID, (e, h) => new { e, h })
                .Where(x => x.h.FState == true && x.h.FDate >= start30 && InBillTypes.Contains(x.h.FBillType ?? -1))
                .Select(x => new { WsId = x.h.FWorkShopID, Num = x.e.FNum })
                .AsEnumerable()
                .GroupBy(x => x.WsId.HasValue && deptNames.ContainsKey(x.WsId.Value) ? deptNames[x.WsId.Value] : "未分配")
                .Select(g => (object)new { name = g.Key, value = g.Sum(x => x.Num ?? 0) })
                .OrderByDescending(o => ((dynamic)o).value)
                .Take(10)
                .ToList();

            // —— 图表 C：库存按仓库构成（t_ERP_ITEM.WarehouseId → t_ERP_Department(IsDEP=1)）——
            vm.StockByWh = db.t_ERP_ITEM
                .Join(db.t_ERP_Department, i => i.WarehouseId, d => d.ID, (i, d) => new { i, d })
                .Where(x => x.d.IsDEP == 1 && (x.i.StockQuantity ?? 0) != 0)
                .Select(x => new { Wh = x.d.DEPName, Qty = x.i.StockQuantity })
                .AsEnumerable()
                .GroupBy(x => x.Wh ?? "未分配")
                .Select(g => (object)new { name = g.Key, value = g.Sum(x => x.Qty ?? 0) })
                .OrderByDescending(o => ((dynamic)o).value)
                .Take(10)
                .ToList();

            // —— 列表：最新生产订单 Top10 ——
            var poPlan = db.t_PMS_ProduceOrderProductEntry
                .GroupBy(p => p.FInterID)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.FPlanNum ?? 0));
            vm.RecentOrders = orders
                .OrderByDescending(o => o.FDate)
                .Take(10)
                .Select(o => new DashPoRow
                {
                    FBillNo = o.FBillNo,
                    FDate = o.FDate,
                    WorkshopName = o.FWorkShopID.HasValue && deptNames.ContainsKey(o.FWorkShopID.Value) ? deptNames[o.FWorkShopID.Value] : "—",
                    StatusLabel = o.FStatus == 0 ? "草稿"
                        : (o.FOrderStatus ?? 0) == 2 ? "已完成"
                        : (o.FOrderStatus ?? 0) == 1 ? "已领料" : "进行中",
                    PlanQty = poPlan.ContainsKey(o.FInterID) ? poPlan[o.FInterID] : 0
                })
                .ToList();

            // —— 列表：库存预警 Top10（库存最低，含缺货/偏低阈值高亮）——
            vm.LowStockThreshold = LowStockThreshold;
            var whNames = db.t_ERP_Department.Where(d => d.IsDEP == 1).ToDictionary(d => d.ID, d => d.DEPName);
            vm.StockAlerts = items
                .OrderBy(i => i.StockQuantity ?? 0)
                .Take(10)
                .Select(i =>
                {
                    var q = i.StockQuantity ?? 0;
                    var lvl = q <= 0 ? "缺货" : (q <= LowStockThreshold ? "偏低" : "");
                    return new DashStockAlertRow
                    {
                        ItemName = i.ItemFullName ?? i.ItemCode ?? "—",
                        WarehouseName = i.WarehouseId.HasValue && whNames.ContainsKey(i.WarehouseId.Value) ? whNames[i.WarehouseId.Value] : "—",
                        StockNum = q,
                        AlertLevel = lvl
                    };
                })
                .ToList();

            // —— 列表：待处理要货 Top10 ——
            vm.PendingApplies = db.t_PMS_GoodsApply
                .Where(a => a.FCancellation == false && a.FStatus != 1)
                .OrderByDescending(a => a.FDate)
                .Take(10)
                .Select(a => new DashApplyRow
                {
                    FBillNo = a.FBillNo,
                    FDate = a.FDate,
                    DeptName = a.FWorkShopID.HasValue && deptNames.ContainsKey(a.FWorkShopID.Value) ? deptNames[a.FWorkShopID.Value] : "—",
                    StatusLabel = a.FCancellation ? "已作废" : (a.FStatus == 1 ? "已审核" : "待审核")
                })
                .ToList();

            return vm;
        }
    }
}
