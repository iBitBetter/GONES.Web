using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// Warehouse picking bill (CKLLD) edit view model. Mirrors the legacy WinForms layout
    /// Stock.FrmPickingOutStockManager (\u4ed3\u5e93\u9886\u6599\u8f93\u5165, t_ERP_Menu.ID=1293) which is the
    /// \u51fa\u5e93\u5355 (stock-out) form. Header has \u51fa\u5e93\u5355\u53f7/\u51fa\u5e93\u65e5\u671f/\u5236\u5355\u4eba/\u6458\u8981 +
    /// \u9886\u6599\u90e8\u95e8/\u9886\u6599\u8f66\u95f4/\u9886\u6599\u4eba (3-level cascade). Table has 13 columns
    /// (BOMID/\u751f\u4ea7\u5355\u53f7/\u5de5\u5e8f\u540d\u79f0/\u4ea7\u54c1\u540d\u79f0/\u89c4\u683c\u578b\u53f7/\u4ea7\u54c1\u5206\u7c7b/\u5355\u4f4d/
    /// \u6279\u6b21\u5e93\u5b58/\u9886\u7528\u6570\u91cf/\u51fa\u5e93\u6570\u91cf/\u6279\u53f7/\u4ed3\u5e93\u540d\u79f0/\u5907\u6ce8) with
    /// NO price/tax columns.
    /// Tables: t_PMS_StockBill (FBillType=8) + t_PMS_StockBillEntry. Out-stock: FROB = -1, consumes
    /// existing batches via FIFO, does NOT create new batch master rows.
    ///
    /// Data source: a production-order picking application (t_PMS_BillUse). Importing one pulls its
    /// FIsProduct=0 rows (raw materials / packing), FIFO-allocates batches, and pre-fills the table
    /// rows. On audit we write back t_PMS_BillUseEntry.FCurrentUseNum += issued qty to prevent
    /// re-import. The production order's own t_PMS_BillUse is NOT modified beyond FCurrentUseNum.
    /// </summary>
    public class WarehousePickEditViewModel
    {
        public int InterId { get; set; }            // FInterID
        public string BillNo { get; set; }          // FBillNo (regenerated on save)
        public DateTime Date { get; set; }          // FDate
        public int? DeptId { get; set; }            // FDeptID   = t_ERP_Department (\u9886\u6599\u90e8\u95e8, IsDEP=0)
        public int? WorkShopId { get; set; }        // FWorkShopID = t_ERP_Department (\u9886\u6599\u8f66\u95f4, IsDEP=2)
        public int? GroupId { get; set; }           // FGroupID = t_ERP_Department (\u9886\u6599\u5c0f\u7ec4, IsDEP=3, ParentID=workshop)
        public int? ManagerId { get; set; }         // FManagerID = t_PMS_Worker (\u9886\u6599\u4eba)
        public string Remark { get; set; }          // FRemark (= \u6458\u8981)
        public bool State { get; set; }             // FState: false=draft true=audited
        public int? FromBillUseId { get; set; }     // t_PMS_BillUse.FInterID (import source)
        public string FromBillUseNo { get; set; }   // t_PMS_BillUse.FBillNo (display)
        public List<WarehousePickRowInput> Rows { get; set; } = new List<WarehousePickRowInput>();
    }

    /// <summary>
    /// One out-stock row, posted from the form. The visible cells map to the legacy 13-column
    /// \u51fa\u5e93\u5355 table. Editable: OutNum, BatchNo, Note. Read-only (display + hidden round-trip):
    /// BomId/StepId/ProduceNo/ProduceId/StepName/ProductName/Spec/CategoryName/Unit/BatchStock/UseNum/
    /// WarehouseName/BillUseEntryId/BillUseNo. OutNum becomes FNum on save (NO price stored).
    /// </summary>
    public class WarehousePickRowInput
    {
        public int ItemId { get; set; }             // FItemID (t_ERP_ITEM.ID) - hidden
        public int? BomId { get; set; }             // BOMID (= t_PMS_BillUseEntry.FBomId) - hidden -> FBomId
        public int? StepId { get; set; }            // = t_PMS_BillUseEntry.FStepID - hidden -> FStepID
        public int? ProduceId { get; set; }         // = t_PMS_BillUseEntry.FProduceId - hidden -> FProduceID
        public string ProduceNo { get; set; }       // \u751f\u4ea7\u5355\u53f7 (t_PMS_BillUseEntry.FProduceNo) - hidden -> FProduceNo
        public string StepName { get; set; }        // \u5de5\u5e8f\u540d\u79f0 (t_PMS_Step.FName) - display
        public string ProductName { get; set; }     // \u4ea7\u54c1\u540d\u79f0 (t_ERP_ITEM.ItemShortName) - display
        public string Spec { get; set; }            // \u89c4\u683c\u578b\u53f7 (t_ERP_ITEM.ItemSpec) - display
        public string CategoryName { get; set; }    // \u4ea7\u54c1\u5206\u7c7b (t_ERP_ITEM.ProductCategory) - display
        public string Unit { get; set; }            // \u5355\u4f4d (t_ERP_ITEM.BaseUnit) - display
        public decimal? BatchStock { get; set; }    // \u6279\u6b21\u5e93\u5b58 (BatchQty for FBatchNo) - display, read-only
        public decimal? UseNum { get; set; }        // \u9886\u7528\u6570\u91cf (t_PMS_BillUseEntry.FUseNum) - display, read-only
        public decimal? OutNum { get; set; }        // \u51fa\u5e93\u6570\u91cf (editable, -> FNum)
        public string BatchNo { get; set; }         // \u6279\u53f7 (editable, FIFO pre-filled)
        public string WarehouseName { get; set; }   // \u4ed3\u5e93\u540d\u79f0 (t_ERP_ITEM.WarehouseId -> t_ERP_Department(IsDEP=1).DEPName) - display
                                                    // \u26a0 \u5207\u52ff\u53d6 t_PMS_BatchNoStock.FName\uff0c\u90a3\u662f\u5efa\u6279\u64cd\u4f5c\u5458\uff08\u5b9e\u6d4b\u6052\u4e3a\u300c\u8d85\u7ea7\u7ba1\u7406\u5458\u300d\uff09\uff0c\u4e0d\u662f\u4ed3\u5e93\u3002
        public string Note { get; set; }            // \u5907\u6ce8 (editable, -> FNote)
        // Back-reference to the source picking application row (write-back on audit).
        public int? BillUseEntryId { get; set; }    // t_PMS_BillUseEntry.FEntryID
        public string BillUseNo { get; set; }       // t_PMS_BillUse.FBillNo
    }

    /// <summary>One row in the "import from picking application" list.</summary>
    public class BillUseImportRow
    {
        public int FInterID { get; set; }
        public string FBillNo { get; set; }
        public DateTime? FDate { get; set; }
        public int? FDeptID { get; set; }
        public int? FWorkShopID { get; set; }
        public int RawCount { get; set; }      // FIsProduct=0 rows still having un-issued qty
        public string DeptName { get; set; }
    }

    /// <summary>Flat list row for the warehouse picking bill list view.</summary>
    public class WarehousePickListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        /// <summary>
        /// False when the bill is locked by a downstream audited production stock-in (its materials
        /// were already consumed). The list then renders un-audit as a disabled button; the server
        /// guard in UnAudit enforces the same rule. Default true (draft / not locked).
        /// </summary>
        public bool CanUnAudit { get; set; } = true;
        /// <summary>
        /// False when the bill's entries are not ALL inside the current user's depot scope (W1:
        /// "读可以少看，动必须整套归你"). The list renders 修改/审核/删除 as disabled buttons; the
        /// server guards in Edit/Audit/Delete/UnAudit enforce the same rule via
        /// <c>DepotScope.BillFullyInScope</c>. Default true (admin / unrestricted).
        /// </summary>
        public bool CanModify { get; set; } = true;
        public string DeptName { get; set; }
        public string GroupName { get; set; }
        public string ManagerName { get; set; }
        public string FromBillUseNo { get; set; }

        public int EntryId { get; set; }
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }
        public string Cplb { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string UnitName { get; set; }
        public decimal? Qty { get; set; }
        public string Note { get; set; }
    }
}
