using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>One manual row of the production in-stock bill (SCRKD).</summary>
    public class ProductInRowInput
    {
        /// <summary>Import source: 0 = produce order, 1 = warehouse pick bill (FB=8, non-packaging step).</summary>
        public int Source { get; set; }
        /// <summary>t_PMS_ProduceOrderProductEntry.FEntryID the row was imported from.</summary>
        public int ProductEntryId { get; set; }
        public int ProduceId { get; set; }
        public string ProduceNo { get; set; }
        /// <summary>Source warehouse-pick bill (FB=8) FInterID / FEntryID.</summary>
        public int PickBillId { get; set; }
        public int PickEntryId { get; set; }
        public string PickBillNo { get; set; }
        public int BomId { get; set; }
        /// <summary>Step FItemID (t_PMS_Step). Derived server-side but also posted for round-trip stability.</summary>
        public int StepId { get; set; }
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string ProductName { get; set; }
        public string Spec { get; set; }
        public string CategoryName { get; set; }
        public string Unit { get; set; }
        public string StepName { get; set; }
        public decimal? PlanNum { get; set; }
        public decimal? InNum { get; set; }
        /// <summary>Display-only 单价：商品出厂价 t_ERP_ITEM.FactoryPrice。**服务端派生，永不采信客户端提交值**。</summary>
        public decimal? Price { get; set; }
        /// <summary>Display-only 金额：入库数量 × 单价（单价×数量=金额，保证自洽）。</summary>
        public decimal? Amount { get; set; }
        public string Note { get; set; }
        /// <summary>Server-derived: the source produce row's picking application was never
        /// issued (no audited FB=8 pick bill). Row is rejected with a dedicated message.</summary>
        public bool Unpicked { get; set; }
    }

    public class ProductInEditViewModel
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime Date { get; set; }
        public int? DeptId { get; set; }
        public int? WorkShopId { get; set; }
        public int? ManagerId { get; set; }
        public string Inspectors { get; set; }
        public string Remark { get; set; }
        public bool State { get; set; }
        public List<ProductInRowInput> Rows { get; set; }
    }

    /// <summary>Flattened header+entry row for the list page (one row per entry).</summary>
    public class ProductInListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        public int EntryId { get; set; }
        public string DeptName { get; set; }
        public string WorkShopName { get; set; }
        public string ManagerName { get; set; }
        public string ProduceNo { get; set; }
        public string StepName { get; set; }
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }
        public string Cplb { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string UnitName { get; set; }
        public decimal? PlanNum { get; set; }
        public decimal? Qty { get; set; }
        /// <summary>单价：明细落库价，回落商品出厂价 ccdj（兼容改造前写入的历史空值行）。</summary>
        public decimal? Price { get; set; }
        /// <summary>金额：单价 × 入库数量。</summary>
        public decimal? Amount { get; set; }
        public string Note { get; set; }

        /// <summary>数据闸门③-b：与服务端 W1 同源——明细未全部落在本仓 ⇒ 灰掉修改/审核/删除。</summary>
        public bool CanModify { get; set; } = true;
    }

    /// <summary>One selectable produce-order product row in the import modal.</summary>
    public class ProductInPickRow
    {
        /// <summary>Import source: 0 = produce order, 1 = warehouse pick bill.</summary>
        public int Source { get; set; }
        public int ProduceId { get; set; }
        public string ProduceNo { get; set; }
        /// <summary>True when the produce order has an AUDITED warehouse pick bill (FB=8)
        /// referencing it (entry FProduceID). Unpicked orders cannot be stocked in.</summary>
        public bool PickState { get; set; }
        public DateTime? ProduceDate { get; set; }
        public int ProductEntryId { get; set; }
        /// <summary>Source warehouse-pick bill (FB=8) FInterID / FEntryID.</summary>
        public int PickBillId { get; set; }
        public int PickEntryId { get; set; }
        public string PickBillNo { get; set; }
        public int BomId { get; set; }
        public int StepId { get; set; }
        public string StepName { get; set; }
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string Cplb { get; set; }
        public string Unit { get; set; }
        public decimal? PlanNum { get; set; }
        /// <summary>出厂价 t_ERP_ITEM.FactoryPrice，弹窗内即时显示单价/金额用。</summary>
        public decimal? Ccdj { get; set; }
        /// <summary>Remaining qty not yet stocked in (FPlanNum - cumulative audited stock-in). Default InNum in the modal.</summary>
        public decimal? RestNum { get; set; }
        /// <summary>Total out-stock qty of the pick bill's BOM inputs (raw materials), shown in the modal.</summary>
        public decimal? OutQty { get; set; }
        /// <summary>BOM output products (待入库产成品) to create as detail rows on import.</summary>
        public List<ProductInPickOutput> Outputs { get; set; } = new List<ProductInPickOutput>();
    }

    /// <summary>One BOM output product (待入库产成品) carried by a pick-bill import row.</summary>
    public class ProductInPickOutput
    {
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string Cplb { get; set; }
        public string Unit { get; set; }
        public decimal? PlanNum { get; set; }
        /// <summary>出厂价 t_ERP_ITEM.FactoryPrice，导入后即时显示单价/金额用。</summary>
        public decimal? Ccdj { get; set; }
        public decimal? RestNum { get; set; }
    }
}
