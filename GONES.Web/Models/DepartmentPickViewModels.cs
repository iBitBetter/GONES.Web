using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>One manual row of the department picking bill (BMCLLD).</summary>
    public class DepartmentPickRowInput
    {
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string ProductName { get; set; }
        public string Spec { get; set; }
        public string CategoryName { get; set; }
        public string Unit { get; set; }
        public int? StepId { get; set; }        // 工序 t_PMS_Step.FItemID（0/空 = 未指定；旧数据为 0）
        public string StepName { get; set; }    // 工序名称 -- display only (join t_PMS_Step)
        public decimal? BatchStock { get; set; }
        public decimal? OutNum { get; set; }
        public string BatchNo { get; set; }
        public string WarehouseName { get; set; }
        public string Note { get; set; }
    }

    public class DepartmentPickEditViewModel
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime Date { get; set; }
        public int? DeptId { get; set; }
        public int? WorkShopId { get; set; }
        public int? ManagerId { get; set; }
        public string Remark { get; set; }
        public bool State { get; set; }
        public List<DepartmentPickRowInput> Rows { get; set; }
    }

    /// <summary>Flattened header+entry row for the list page (one row per entry).</summary>
    public class DepartmentPickListRow
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
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }
        public string Cplb { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string UnitName { get; set; }
        public decimal? Qty { get; set; }
        public string Note { get; set; }

        /// <summary>数据闸门③-b：与服务端 W1 同源——明细未全部落在本仓 ⇒ 灰掉修改/审核/删除。</summary>
        public bool CanModify { get; set; } = true;
    }
}
