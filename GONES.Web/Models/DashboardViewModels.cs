using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 生产 MES 数据看板（控制台首页大屏）视图模型。
    /// 服务端一次性算好所有 KPI / 图表序列 / 列表，首页直接渲染；
    /// 同时经 /Dashboard/Data 以 JSON 形式输出，供前端 30s 自动刷新增量更新。
    /// </summary>
    public class DashboardViewModel
    {
        // —— KPI 卡片 ——
        public int PoTotal { get; set; }           // 生产订单总数
        public int PoDraft { get; set; }            // 草稿(未审核)
        public int PoInProgress { get; set; }       // 进行中(已审未入库)
        public int PoCompleted { get; set; }        // 已完成(已入库)
        public int PoThisMonth { get; set; }       // 本月新增
        public decimal TodayOutput { get; set; }   // 今日生产入库量
        public int StockKinds { get; set; }        // 有库存物料种类
        public decimal StockTotalQty { get; set; } // 库存总量
        public int PendingApply { get; set; }      // 待处理要货单
        public decimal MonthLossQty { get; set; }  // 本月报损量
        public decimal MonthLossAmount { get; set; } // 本月报损金额
        public int WorkshopCount { get; set; }     // 车间数
        public int StepCount { get; set; }         // 工序数

        // —— 图表 A：近30天出入库趋势 ——
        public List<string> TrendDates { get; set; } = new List<string>();
        public List<decimal> TrendIn { get; set; } = new List<decimal>();
        public List<decimal> TrendOut { get; set; } = new List<decimal>();

        // —— 图表 B：近30天各车间入库量(全部入库类型) ——
        public List<object> WorkshopOutput { get; set; } = new List<object>(); // [{name,value}]

        // —— 库存预警阈值（FStockNum <= 此值视为偏低，<=0 视为缺货）——
        public decimal LowStockThreshold { get; set; } = 500m;

        // —— 图表 C：库存按仓库构成 ——
        public List<object> StockByWh { get; set; } = new List<object>(); // [{name,value}]

        // —— 列表 ——
        public List<DashPoRow> RecentOrders { get; set; } = new List<DashPoRow>();
        public List<DashStockAlertRow> StockAlerts { get; set; } = new List<DashStockAlertRow>();
        public List<DashApplyRow> PendingApplies { get; set; } = new List<DashApplyRow>();
    }

    public class DashPoRow
    {
        public string FBillNo { get; set; }
        public DateTime? FDate { get; set; }
        public string WorkshopName { get; set; }
        public string StatusLabel { get; set; }
        public decimal PlanQty { get; set; }
    }

    public class DashStockAlertRow
    {
        public string ItemName { get; set; }
        public string WarehouseName { get; set; }
        public decimal StockNum { get; set; }
        /// <summary>预警级别：缺货(<=0) / 偏低(<=阈值) / 空串(正常)。</summary>
        public string AlertLevel { get; set; } = "";
    }

    public class DashApplyRow
    {
        public string FBillNo { get; set; }
        public DateTime? FDate { get; set; }
        public string DeptName { get; set; }
        public string StatusLabel { get; set; }
    }
}
