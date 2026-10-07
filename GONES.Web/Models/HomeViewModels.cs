using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>首页快捷入口（按角色生成）。</summary>
    public class HomeQuickLink
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>图标键，视图端据此渲染内联 SVG。</summary>
        public string Icon { get; set; } = "";
    }

    /// <summary>控制台首页视图模型 —— 门店系统风格：生产数据统计 + 快捷入口 + 报表统计。</summary>
    public class HomeViewModel
    {
        public string UserName { get; set; } = "";
        public bool IsAdmin { get; set; }

        /// <summary>统计期间键：day/week/month/quarter/year。</summary>
        public string PeriodKey { get; set; } = "day";
        /// <summary>期间显示名：今日/本周/本月/本季/今年。</summary>
        public string PeriodLabel { get; set; } = "今日";
        /// <summary>瓦片上的小徽章文字（同期间名）。</summary>
        public string BadgeText { get; set; } = "今日";
        public DateTime Start { get; set; }
        /// <summary>期间右开端（不含）。</summary>
        public DateTime End { get; set; }

        // —— 期间统计（已审单据）——
        public decimal InQty { get; set; }
        public decimal InQtyProductIn { get; set; }   // 其中生产入库(FB=1)
        public decimal OutQty { get; set; }
        public decimal OutQtyPick { get; set; }       // 其中领料出库(FB=8/9)
        public int BillCount { get; set; }            // 期间单据数（去重 FInterID）
        public int InBillCount { get; set; }
        public int OutBillCount { get; set; }
        public decimal LossQty { get; set; }          // 报损数量(FB=5)
        public decimal LossAmount { get; set; }       // 报损金额

        // —— 静态 KPI ——
        public int PendingApply { get; set; }         // 待处理要货
        public int StockKinds { get; set; }           // 有库存物料种数
        public decimal StockTotalQty { get; set; }    // 库存总量

        // —— 报表统计：近 30 天趋势（数量）——
        public List<string> TrendDates { get; set; } = new();
        public List<decimal> TrendIn { get; set; } = new();
        public List<decimal> TrendOut { get; set; } = new();
        public List<decimal> TrendLoss { get; set; } = new();

        public List<HomeQuickLink> Links { get; set; } = new();
    }
}
