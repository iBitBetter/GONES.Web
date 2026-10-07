using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;

namespace GONES.Web.Services
{
    /// <summary>
    /// 控制台首页数据构建器 —— 与 DashboardData 同口径（入库/出库方向、已审单据、FNum 账本数量）。
    /// 期间统计 + 近30天趋势 + 按角色快捷入口。
    /// </summary>
    public static class HomeStatsData
    {
        // 入库 / 出库方向（与 DashboardData、全站账本口径一致）
        private static readonly int[] InTypes = { 0, 1, 2, 3, 4, 6, 7 };
        private static readonly int[] OutTypes = { 5, 8, 9, 10 };
        private static readonly int[] PickTypes = { 8, 9 }; // 仓库领料 / 部门领料

        // —— 快捷入口目录（按角色分池；随后按账号实际菜单权限过滤，无权限者不显示）——
        private static readonly List<HomeQuickLink> AdminLinks = new List<HomeQuickLink>
        {
            new() { Title = "采购入库", Url = "/StockBill", Icon = "box" },
            new() { Title = "生产入库", Url = "/ProductIn", Icon = "inbox" },
            new() { Title = "仓库领料", Url = "/WarehousePick", Icon = "truck" },
            new() { Title = "部门领料", Url = "/DepartmentPick", Icon = "users" },
            new() { Title = "入出库统计", Url = "/ProductInOutCount", Icon = "chart" },
            new() { Title = "库存统计", Url = "/StockStat", Icon = "database" },
            new() { Title = "数据看板", Url = "/Dashboard", Icon = "dashboard" },
            new() { Title = "系统管理", Url = "/SysMenu", Icon = "settings" }
        };

        private static readonly List<HomeQuickLink> UserLinks = new List<HomeQuickLink>
        {
            new() { Title = "采购入库", Url = "/StockBill", Icon = "box" },
            new() { Title = "生产入库", Url = "/ProductIn", Icon = "inbox" },
            new() { Title = "仓库领料", Url = "/WarehousePick", Icon = "truck" },
            new() { Title = "部门领料", Url = "/DepartmentPick", Icon = "users" },
            new() { Title = "要货申请", Url = "/GoodsApply", Icon = "cart" },
            new() { Title = "入出库统计", Url = "/ProductInOutCount", Icon = "chart" },
            new() { Title = "库存统计", Url = "/StockStat", Icon = "database" },
            new() { Title = "批号对账", Url = "/BatchStock", Icon = "check" }
        };

        /// <summary>
        /// 构建首页数据。
        /// <paramref name="allowedUrls"/> = 当前账号实际可访问的路由集合（来自 MenuService.GetAccessibleUrls，
        /// 与侧边栏菜单同源）。传入时按它过滤快捷入口 —— 没有权限的入口直接隐藏；传 null 时按角色池原样返回。
        /// </summary>
        public static HomeViewModel Build(GonesPgDbContext db, string periodKey, bool isAdmin, string userName,
            HashSet<string> allowedUrls = null)
        {
            var vm = new HomeViewModel { UserName = userName, IsAdmin = isAdmin };
            var today = DateTime.Today;

            // —— 期间解析 ——
            DateTime start, end;
            switch ((periodKey ?? "").ToLower())
            {
                case "week":
                {
                    var dow = (int)today.DayOfWeek; if (dow == 0) dow = 7; // 周一为一周之始
                    start = today.AddDays(1 - dow); end = start.AddDays(7);
                    vm.PeriodLabel = "本周"; break;
                }
                case "month":
                    start = new DateTime(today.Year, today.Month, 1); end = start.AddMonths(1);
                    vm.PeriodLabel = "本月"; break;
                case "quarter":
                    start = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1); end = start.AddMonths(3);
                    vm.PeriodLabel = "本季"; break;
                case "year":
                    start = new DateTime(today.Year, 1, 1); end = start.AddYears(1);
                    vm.PeriodLabel = "今年"; break;
                default:
                    start = today; end = today.AddDays(1);
                    vm.PeriodLabel = "今日"; break;
            }
            vm.PeriodKey = (periodKey ?? "day").ToLower();
            vm.BadgeText = vm.PeriodLabel;
            vm.Start = start; vm.End = end;

            // —— 一次取数：已审单据明细（起点 = min(期间起点, 29天前)），客户端聚合 ——
            var start30 = today.AddDays(-29);
            var fetchStart = start < start30 ? start : start30;
            var rows = db.t_PMS_StockBillEntry
                .Join(db.t_PMS_StockBill, e => e.FInterID, h => h.FInterID, (e, h) => new { e, h })
                .Where(x => x.h.FState == true && x.h.FDate >= fetchStart)
                .Select(x => new { Date = x.h.FDate, InterId = x.e.FInterID, BT = x.h.FBillType ?? -1, Num = x.e.FNum ?? 0m, Amt = x.e.FAmount ?? 0m })
                .ToList()
                .Where(x => x.Date.HasValue)
                .Select(x => new { Date = x.Date.Value.Date, x.InterId, x.BT, x.Num, x.Amt })
                .ToList();

            // —— 期间统计 ——
            var period = rows.Where(r => r.Date >= start && r.Date < end).ToList();
            vm.InQty = period.Where(r => InTypes.Contains(r.BT)).Sum(r => r.Num);
            vm.InQtyProductIn = period.Where(r => r.BT == 1).Sum(r => r.Num);
            vm.OutQty = period.Where(r => OutTypes.Contains(r.BT)).Sum(r => r.Num);
            vm.OutQtyPick = period.Where(r => PickTypes.Contains(r.BT)).Sum(r => r.Num);
            vm.BillCount = period.Select(r => r.InterId).Distinct().Count();
            vm.InBillCount = period.Where(r => InTypes.Contains(r.BT)).Select(r => r.InterId).Distinct().Count();
            vm.OutBillCount = period.Where(r => OutTypes.Contains(r.BT)).Select(r => r.InterId).Distinct().Count();
            vm.LossQty = period.Where(r => r.BT == 5).Sum(r => r.Num);
            vm.LossAmount = period.Where(r => r.BT == 5).Sum(r => r.Amt);

            // —— 静态 KPI（与看板同口径）——
            vm.PendingApply = db.t_PMS_GoodsApply.Count(a => a.FCancellation == false && a.FStatus != 1);
            // DB 侧聚合：不要 ToList() 全表拉取 —— 首页是最高频页面，商品表大时开销显著。
            vm.StockKinds = db.t_ERP_ITEM.Count(i => (i.StockQuantity ?? 0) > 0);
            vm.StockTotalQty = db.t_ERP_ITEM.Sum(i => i.StockQuantity ?? 0);

            // —— 近 30 天趋势（入库/出库/报损 数量）——
            var trend = rows.Where(r => r.Date >= start30)
                .GroupBy(r => r.Date)
                .ToDictionary(g => g.Key, g => new
                {
                    In = g.Where(r => InTypes.Contains(r.BT)).Sum(r => r.Num),
                    Out = g.Where(r => OutTypes.Contains(r.BT)).Sum(r => r.Num),
                    Loss = g.Where(r => r.BT == 5).Sum(r => r.Num)
                });
            for (var d = start30; d <= today; d = d.AddDays(1))
            {
                vm.TrendDates.Add(d.ToString("MM-dd"));
                var t = trend.ContainsKey(d) ? trend[d] : null;
                vm.TrendIn.Add(t?.In ?? 0);
                vm.TrendOut.Add(t?.Out ?? 0);
                vm.TrendLoss.Add(t?.Loss ?? 0);
            }

            // —— 快捷入口（按角色池 + 账号实际权限过滤）——
            // allowedUrls 为 null 时退回角色池原样（兼容其它调用方）；否则只保留账号有权限的入口。
            var pool = isAdmin ? AdminLinks : UserLinks;
            vm.Links = allowedUrls == null
                ? new List<HomeQuickLink>(pool)
                : pool.Where(l => allowedUrls.Contains(l.Url)).ToList();

            return vm;
        }
    }
}
