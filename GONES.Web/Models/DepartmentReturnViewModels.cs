using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>One return row of the department-return form.</summary>
    public class DepartmentReturnRowInput
    {
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string ProductName { get; set; }
        public string Spec { get; set; }
        public string CategoryName { get; set; }
        public string Unit { get; set; }

        // 工序（t_PMS_Step.FItemID；0=未指定）。旧 FrmDepartmentReturn 有可编辑的
        // 「工序名称」列（gc_FStepName / cbb_StepName），Web 版对齐补齐（2026-09-12）。
        public int? StepId { get; set; }
        public string StepName { get; set; }

        public decimal? BatchStock { get; set; }
        public string BatchNo { get; set; }
        public string WarehouseName { get; set; }

        // Import provenance. Non-null pair => row was imported from an audited
        // department picking bill; null pair => manually added item row.
        public int? PickBillId { get; set; }
        public string PickBillNo { get; set; }
        public int? PickEntryId { get; set; }
        public decimal? PickNum { get; set; }

        // Legacy FrmDepartmentReturn has a SINGLE return-quantity column -> FNum.
        public decimal? Num { get; set; }

        public string Note { get; set; }
    }

    public class DepartmentReturnEditViewModel
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime Date { get; set; }
        public int? DeptId { get; set; }
        public int? WorkShopId { get; set; }
        public int? ManagerId { get; set; }
        public string Remark { get; set; }
        public bool State { get; set; }
        public List<DepartmentReturnRowInput> Rows { get; set; }
    }

    /// <summary>Flattened list-page row (header fields repeated per entry, like the legacy grid).</summary>
    public class DepartmentReturnListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        public string DeptName { get; set; }
        public string WorkShopName { get; set; }
        public string ManagerName { get; set; }

        public string FromPickBillNo { get; set; }
        public int EntryId { get; set; }
        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string Cplb { get; set; }
        public string UnitName { get; set; }
        public string BatchNo { get; set; }
        public decimal? Num { get; set; }
        public string Note { get; set; }

        /// <summary>数据闸门③-b：与服务端 W1 同源——明细未全部落在本仓 ⇒ 灰掉修改/审核/删除。</summary>
        public bool CanModify { get; set; } = true;
    }
}
