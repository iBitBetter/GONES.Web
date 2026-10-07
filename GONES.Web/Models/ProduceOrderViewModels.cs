using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// Production order edit view model.
    /// Old WinForms: Production.FrmProductionOrderManager / FrmProductionOrderEdit.
    /// Tables: t_PMS_ProduceOrder (header)
    ///       + t_PMS_ProduceOrderProductEntry     (output products)
    ///       + t_PMS_ProduceOrderStepEntry        (process steps, derived from BOM)
    ///       + t_PMS_ProduceOrderRawMaterialEntry (raw materials, derived from BOM)
    ///
    /// Business rules kept from the old system:
    ///   - FBillNo = "SCD" + 6-digit sequence (max+1); FInterID = max+1 (both are manual, not identity).
    ///   - FStatus 0 = unaudited, 1 = audited; audit writes FAuditorID / FAuditDate / FComment.
    ///   - FOrderStatus 0 = not started, 1 = materials picked, 2 = stocked in;
    ///     unaudit requires FStatus=1 AND FOrderStatus=0.
    ///   - A product can be planned only when it already has a BOM (BOM output row supplies FBomId).
    ///   - Step + raw material entries are EXPANDED from the BOM on the server (never hand-edited).
    /// </summary>
    public class ProduceOrderEditViewModel
    {
        public int InterId { get; set; }            // FInterID
        public string BillNo { get; set; }          // FBillNo ("SCD" + 6 digits)
        public DateTime Date { get; set; }          // FDate
        public int? DeptId { get; set; }            // FDeptID = t_ERP_Department.ID
        public int? ApplyerId { get; set; }         // FApplyerID = t_PMS_Worker.ID
        public string ApplyNo { get; set; }         // FApplyNo = source goods-apply bill no
        public string ScheduleNo { get; set; }      // FScheduleNo (production schedule no, free text)
        public string Remark { get; set; }          // FRemark
        public short Status { get; set; }           // FStatus: 0 unaudited / 1 audited
        public short? OrderStatus { get; set; }     // FOrderStatus: 0/1/2
        public List<ProduceOrderRowInput> Rows { get; set; } = new List<ProduceOrderRowInput>();

        // ---- picking bill (ling liao dan) generated together with the produce order ----
        // There is no separate dispatch step: t_PMS_BillUse / t_PMS_BillUseEntry are written
        // on save, ONE picking bill per product. ALL picking header fields (dept / workshop /
        // group / worker / date / work people / remark) live on each product ROW
        // (see ProduceOrderRowInput) — they are entered in the add-product modal.
        public string PickNo { get; set; }          // display only: generated picking bill no(s)

        /// <summary>Posted picking quantities; only UseNum is trusted, everything else is re-derived server-side.</summary>
        public List<ProduceOrderPickRowInput> PickRows { get; set; } = new List<ProduceOrderPickRowInput>();
    }

    /// <summary>One posted picking detail row (raw material qty editable, output qty server-owned).</summary>
    public class ProduceOrderPickRowInput
    {
        public int BomId { get; set; }
        public int ItemId { get; set; }
        public bool IsProduct { get; set; }
        public decimal? UseNum { get; set; }
    }

    /// <summary>One output-product row as posted from the form.</summary>
    public class ProduceOrderRowInput
    {
        public int ItemId { get; set; }             // FItemID (t_ERP_ITEM.ID)
        public decimal? PlanNum { get; set; }       // FPlanNum, must be > 0
        public string ProduceType { get; set; }     // FProduceType, default "\u81ea\u5236" (self-made)

        /// <summary>
        /// Source goods-apply entry (t_PMS_GoodsApplyEntry.FEntryID) this product row was
        /// imported from; 0 / null when the product was added by hand.
        /// Persisted to t_PMS_ProduceOrderProductEntry.FBillApplyEntryID so that production
        /// in-stock can write the finished quantity back to the exact apply row.
        /// </summary>
        public int? SrcEntryId { get; set; }
        // Picking bill header follows the product: each product gets its own t_PMS_BillUse
        // stamped with these values (entered in the add-product modal).
        public int? PickDeptId { get; set; }        // t_PMS_BillUse.FDeptID
        public int? PickWorkShopId { get; set; }    // FWorkShopID
        public int? PickGroupId { get; set; }       // FGroupID
        public int? PickWorkerId { get; set; }      // FWorkerID
        public DateTime? PickDate { get; set; }     // FDate, defaults to the order date
        public string PickWorkPeople { get; set; }  // FWorkPeople, defaults to all workers under the chosen group (or workshop)
        public string PickRemark { get; set; }      // FRemark
        // Derived server-side: FBomId (BOM map), FBatchNumber (= header bill no),
        // FPlanDate (= header date), step + raw material entries (BOM expansion).
    }

    /// <summary>Index page row: one production order per row.</summary>
    public class ProduceOrderListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public short Status { get; set; }           // 0 unaudited / 1 audited
        public short? OrderStatus { get; set; }     // 0 not started / 1 picked / 2 stocked
        public string DeptName { get; set; }
        public string ApplyerName { get; set; }
        public string ApplyNo { get; set; }
        public int ProductCount { get; set; }
        public decimal PlanTotal { get; set; }
        public string ProductSummary { get; set; }
        public string Remark { get; set; }

        /// <summary>
        /// 仓库数据权限（W1）：整单产出未全部落在当前用户管理的仓库内 ⇒ false，
        /// 列表灰掉 修改/审核/删除/反审核。与服务端 InDepotScope 同源。
        /// </summary>
        public bool CanModify { get; set; } = true;
    }

    /// <summary>Read-only process-step row shown on the form (from BOM or from saved entry).</summary>
    public class ProduceOrderStepPreview
    {
        public int? StepId { get; set; }
        public string StepName { get; set; }
        public string Algo { get; set; }
        public decimal? Hour { get; set; }
        public decimal? Num { get; set; }
        public decimal? SingleArti { get; set; }
        public decimal? HourArti { get; set; }
        public decimal? Price { get; set; }
        public string WorkHourUnit { get; set; }
        public string PriceUnit { get; set; }
        public decimal? FixedLoss { get; set; }
        public decimal? LossStandValue { get; set; }
        public string LossRate { get; set; }
        public decimal? LossValue { get; set; }
        public string LossUnit { get; set; }
        public int? BomId { get; set; }
        public int? ItemId { get; set; }
        public string ItemName { get; set; }
    }

    /// <summary>Read-only raw-material row shown on the form (from BOM or from saved entry).</summary>
    public class ProduceOrderMaterialPreview
    {
        public int? BomId { get; set; }
        public int? ItemId { get; set; }
        public string ItemNumber { get; set; }
        public string ItemName { get; set; }
        public string ItemModel { get; set; }
        public string Unit { get; set; }
        public decimal? BaseNum { get; set; }       // FNum  = BOM FBaseNum (per unit)
        public string ItemCategory { get; set; }    // t_ERP_ITEM.ProductCategory (product category name)
        public decimal? StockQty { get; set; }      // t_ERP_ITEM.StockQuantity (stock cache rebuilt from ledger)
        public decimal? PlanNum { get; set; }       // output plan qty used for expansion
        public decimal? NeedNum { get; set; }       // FNeedNum = FBaseNum * plan qty
        public decimal? UseNum { get; set; }        // t_PMS_BillUseEntry.FUseNum (editable for materials)
        public int? StepId { get; set; }            // FStepID, from the BOM row
        public string StepName { get; set; }
        public string WorkHourUnit { get; set; }    // copied from the step (old system behavior)
        public string PriceUnit { get; set; }
        public decimal? FixedLoss { get; set; }
        public decimal? LossStandValue { get; set; }
        public string LossRate { get; set; }
        public decimal? LossValue { get; set; }
        public string LossUnit { get; set; }
    }

    /// <summary>BOM material group: output product + its raw materials.</summary>
    public class ProduceOrderBomMaterialGroup
    {
        public int? BomId { get; set; }
        public string StepName { get; set; }
        public ProduceOrderMaterialPreview Output { get; set; }
        public List<ProduceOrderMaterialPreview> Materials { get; set; } = new List<ProduceOrderMaterialPreview>();
    }
}
