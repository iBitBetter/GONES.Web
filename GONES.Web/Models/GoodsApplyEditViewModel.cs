using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// Goods apply (requisition plan) edit view model.
    /// Old WinForms: Stock.FrmGetGoodsApplyEdit. Tables: t_PMS_GoodsApply + t_PMS_GoodsApplyEntry.
    /// Business rules (from old form):
    ///   - BillNo = "JHD" + 8-digit sequence (max+1), FInterID = max+1 (both manual).
    ///   - Applicant comes from t_PMS_Worker; department (FDeptID) = worker.DEPID (server-side derived).
    ///   - Every entry item MUST have a BOM (t_PMS_StepProductBom output row provides FBomId).
    ///   - Qty must be > 0; FetchDate must be >= today.
    /// </summary>
    public class GoodsApplyEditViewModel
    {
        public int InterId { get; set; }            // FInterID
        public string BillNo { get; set; }          // FBillNo (regenerated on save)
        public DateTime Date { get; set; }          // FDate
        public int? EmpId { get; set; }             // FEmpID   = t_PMS_Worker.ID
        public int? DeptId { get; set; }            // FDeptID  = level-1 department (IsDEP=0), selected in the 3-level cascade
        public int? StockId { get; set; }           // FStockID = shipping warehouse: REMOVED from the form (2026-09-08), null on create, preserved on edit
        public int? WorkShopId { get; set; }        // FWorkShopID = level-2 sub-dept (IsDEP!=0: workshop 2 / warehouse 1 / team 3), child of DeptId
        public string Explanation { get; set; }     // FExplanation
        public short Status { get; set; }           // FStatus: 0=draft 1=audited 2=done
        public List<GoodsApplyRowInput> Rows { get; set; } = new List<GoodsApplyRowInput>();
    }

    /// <summary>One entry row as posted from the form.</summary>
    public class GoodsApplyRowInput
    {
        public int ItemId { get; set; }             // FItemID (t_ERP_ITEM.ID)
        public decimal? Qty { get; set; }           // FQty, must be > 0
        public decimal? Price { get; set; }         // FPrice, default item cbdj
        public DateTime? FetchDate { get; set; }    // FFetchDate, must be >= today
        public string Note { get; set; }            // FNote
        // Derived server-side on save: FBomId (BOM map), FAmount = Qty*Price,
        // FUnitID (unit dict by item wljbdw name), FSecCoefficient/FSecQty = 0.
    }

    /// <summary>Index page row: header summary + resolved names.</summary>
    public class GoodsApplyListItem
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public string DeptName { get; set; }
        public string EmpName { get; set; }
        public string StockName { get; set; }
        public int EntryCount { get; set; }
        public short Status { get; set; }
        public bool Cancelled { get; set; }
        public string Explanation { get; set; }
    }

    /// <summary>
    /// Flat list row in old-system style: one row per entry, header fields
    /// filled only on the first row of each bill (FirstOfBill).
    /// </summary>
    public class GoodsApplyListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public string DeptName { get; set; }
        public string EmpName { get; set; }
        public short Status { get; set; }
        public bool Cancelled { get; set; }
        public string Explanation { get; set; }
        public bool FirstOfBill { get; set; }

        // 是否允许反审核：已审核且存在产品已被生产单引入时为 false（按钮置灰）。
        public bool CanUnAudit { get; set; } = true;

        /// <summary>
        /// 仓库数据权限（W1）：整单明细未全部落在当前用户管理的仓库内 ⇒ false，
        /// 列表灰掉 修改/审核/删除/反审核。与服务端 InDepotScope 同源。
        /// </summary>
        public bool CanModify { get; set; } = true;

        public int EntryId { get; set; }
        public int BomId { get; set; }              // FBomId
        public string Cplb { get; set; }            // item class (cplb)
        public string Cpjc { get; set; }            // item name
        public string Cpgg { get; set; }            // spec
        public string UnitName { get; set; }        // unit dict name by FUnitID
        public decimal? Qty { get; set; }           // FQty
        public decimal? FetchNum { get; set; }      // FFetchNum (completed qty)
        public DateTime? FetchDate { get; set; }    // FFetchDate
        public string Note { get; set; }            // FNote
    }
}
