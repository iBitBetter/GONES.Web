using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 产品结构图汇总行 —— 口径复刻旧存储过程 p_PMS_ProductBomSum：
    /// 领料数量=期间 FB=8/9；退料数量=期间 FB=2/3；实际出库数量=累计(退料-领料)，截至结束日；
    /// 完工入库数量=期间 FB=1；实际出库金额=期间 FB=8/9/2/3 金额；出库单价=最近一次 FB=8&amp;FROB=1 的
    /// FAfterTaxPrice，回落最近一次 FB=4&amp;FROB=1 的 FPrice。
    /// 产成品行（本结构图的产出行，QtyIn&gt;0 且 BomId&gt;0）由 ProductBomStatController.PostProcess 覆写为
    /// 生产入库单(FB=1)落库口径：单价 = ProduceInAmount ÷ QtyIn，金额 = QtyIn × 单价。
    /// 注意：EF6 Database.SqlQuery 物化只映射可写属性（不映射公有字段），必须用 { get; set; }。
    /// </summary>
    public class ProductBomStatRow
    {
        public int BomId { get; set; }
        public string StepName { get; set; }
        public string ItemClass { get; set; }
        public string ItemNumber { get; set; }
        public string ItemName { get; set; }
        public string ItemModel { get; set; }
        public string ItemUnit { get; set; }
        public int StockId { get; set; }
        public string StockName { get; set; }
        public decimal QtyGet { get; set; }
        public decimal QtyBack { get; set; }
        public decimal QtyOut { get; set; }
        public decimal QtyIn { get; set; }
        public decimal? OutPrice { get; set; }
        public decimal OutAmount { get; set; }
        /// <summary>期间 FB=1（生产入库）明细金额合计；产成品行单价的分子。</summary>
        public decimal ProduceInAmount { get; set; }
        /// <summary>最近一次 FB=1 明细单价；仅当期间明细无金额时作兜底。</summary>
        public decimal? ProduceInLastPrice { get; set; }
    }

    /// <summary>按 BomId 分组后的展示组（含组小计，对应旧 DevExpress 组计行）。</summary>
    public class ProductBomStatGroup
    {
        public int BomId { get; set; }
        public List<ProductBomStatRow> Rows { get; set; } = new List<ProductBomStatRow>();
        public decimal QtyGet { get; set; }
        public decimal QtyBack { get; set; }
        public decimal QtyOut { get; set; }
        public decimal QtyIn { get; set; }
        public decimal OutAmount { get; set; }
    }

    /// <summary>工序下拉项（t_PMS_Step 无 EF 实体，走 SqlQuery）。</summary>
    public class ProductBomStatStepOption
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
