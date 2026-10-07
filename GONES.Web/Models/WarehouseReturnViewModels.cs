using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// Warehouse RETURN bill (TIN) edit view model. Mirrors the legacy WinForms
    /// Stock.FrmPickingBackStockManager (仓库退料输入, t_ERP_Menu.ID=1294).
    /// Header t_PMS_StockBill (FBillType=3, FBillTypeEx=仓库退料, prefix TIN) + entries
    /// t_PMS_StockBillEntry. In-stock direction: FROB = +1, batch nos are RE-USED from the
    /// source picking bill (no new batch master rows are created on audit).
    /// Row qty split: FNum = 正常退料, FNumExt1 = 厂家原因, FNumExt2 = 人为原因,
    /// FNumExt3 = 其它原因 -- all positive, direction comes from FROB. The stock ledger
    /// sums FNum + all three Exts.
    /// Data source: an AUDITED warehouse picking bill (FBillType=8, FState=1); its rows are
    /// imported as-is and the operator only fills the four return quantities.
    /// </summary>
    public class WarehouseReturnEditViewModel
    {
        public int InterId { get; set; }            // FInterID
        public string BillNo { get; set; }          // FBillNo (regenerated on save)
        public DateTime Date { get; set; }          // FDate
        public int? DeptId { get; set; }            // FDeptID   = t_ERP_Department (退料部门, IsDEP=0)
        public int? WorkShopId { get; set; }        // FWorkShopID = t_ERP_Department (退料车间, IsDEP=2)
        public int? GroupId { get; set; }           // FGroupID = t_ERP_Department (退料小组, IsDEP=3)
        public int? ManagerId { get; set; }         // FManagerID = t_PMS_Worker (退料人)
        public string Remark { get; set; }          // FRemark
        public bool State { get; set; }             // FState: false=draft true=audited
        public List<WarehouseReturnRowInput> Rows { get; set; } = new List<WarehouseReturnRowInput>();
    }

    /// <summary>
    /// One return row, posted from the form. Trusted from POST: PickBillId + PickEntryId
    /// (locate the source picking-bill row) and the four return quantities + Note. Everything
    /// else is server-rederived from t_PMS_StockBillEntry on save (defense vs. tampered POST).
    /// </summary>
    public class WarehouseReturnRowInput
    {
        public int ItemId { get; set; }             // FItemID - server-rederived
        public int? BomId { get; set; }             // FBomId - server-rederived
        public int? StepId { get; set; }            // FStepID - server-rederived
        public int? ProduceId { get; set; }         // FProduceID - server-rederived
        public string ProduceNo { get; set; }       // 生产单号 - server-rederived
        public string StepName { get; set; }        // 工序名称 - display
        public string ProductName { get; set; }     // 产品名称 - display
        public string Spec { get; set; }            // 规格型号 - display
        public string CategoryName { get; set; }    // 产品分类 - display
        public string Unit { get; set; }            // 单位 - display
        public decimal? BatchStock { get; set; }    // 批次库存 (BatchQty, display)
        public decimal? PickNum { get; set; }       // 领料出库数量 (source FNum, display + cap)
        public int? PickBillId { get; set; }        // source picking bill FInterID - trust key
        public string PickBillNo { get; set; }      // source picking bill FBillNo - display -> FBillUseNo
        public int? PickEntryId { get; set; }       // source t_PMS_StockBillEntry.FEntryID - trust key -> FBillUseEntryID
        public decimal? Num { get; set; }           // 正常退料数量 -> FNum
        public decimal? NumExt1 { get; set; }       // 厂家原因退料数量 -> FNumExt1
        public decimal? NumExt2 { get; set; }       // 人为原因退料数量 -> FNumExt2
        public decimal? NumExt3 { get; set; }       // 其它原因退料数量 -> FNumExt3
        public string BatchNo { get; set; }         // 批号 - server-rederived (from the pick row)
        public string WarehouseName { get; set; }   // 仓库名称 (item.WarehouseId -> DEPName) - display
        public string Note { get; set; }            // 备注 -> FNote
    }

    /// <summary>One row of the "select picking bill" modal list (audited FBillType=8 bills).</summary>
    public class PickBillImportRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public string DeptName { get; set; }
        public string GroupName { get; set; }
        public int RowCount { get; set; }
        public decimal TotalQty { get; set; }
    }

    /// <summary>Flat list row for the warehouse return bill list view.</summary>
    public class WarehouseReturnListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        public string DeptName { get; set; }
        public string GroupName { get; set; }
        public string ManagerName { get; set; }
        public string FromPickBillNo { get; set; }

        public int EntryId { get; set; }
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }
        public string Cplb { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string UnitName { get; set; }
        public decimal? Num { get; set; }
        public decimal? NumExt1 { get; set; }
        public decimal? NumExt2 { get; set; }
        public decimal? NumExt3 { get; set; }
        public decimal RowTotal => (Num ?? 0m) + (NumExt1 ?? 0m) + (NumExt2 ?? 0m) + (NumExt3 ?? 0m);
        public string Note { get; set; }

        /// <summary>数据闸门③-b：与服务端 W1 同源——明细未全部落在本仓 ⇒ 灰掉修改/审核/删除。</summary>
        public bool CanModify { get; set; } = true;
    }
}
