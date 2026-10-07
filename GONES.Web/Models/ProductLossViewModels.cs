using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>One manual row of the product loss bill (CPBSD).</summary>
    public class ProductLossRowInput
    {
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string ProductName { get; set; }
        public string Spec { get; set; }
        public string CategoryName { get; set; }
        public string Unit { get; set; }
        public int? StepId { get; set; }              // 工序 t_PMS_Step.FItemID（0/空 = 未指定）
        public string StepName { get; set; }          // 显示用：服务端按 StepId 从 t_PMS_Step 推导
        public decimal? BatchStock { get; set; }
        public decimal? LossNum { get; set; }
        public string BatchNo { get; set; }
        public string WarehouseName { get; set; }
        public string Note { get; set; }

        // 从仓库退料单（FBillType=3）导入的行：报损「厂家原因 + 人为原因 + 其它原因」退掉的数量
        // （不含正常退料 FNum 列）。ReturnBillId/ReturnEntryId 定位来源退料明细行；
        // FactoryNum/HumanNum/OtherNum/RestNum 均由服务端推导（防篡改），表单里的值一律覆盖。
        public int? ReturnBillId { get; set; }
        public int? ReturnEntryId { get; set; }
        public string ReturnBillNo { get; set; }
        public decimal? FactoryNum { get; set; }     // 厂家原因退料量 (FNumExt1)
        public decimal? HumanNum { get; set; }       // 人为原因退料量 (FNumExt2)
        public decimal? OtherNum { get; set; }       // 其它原因退料量 (FNumExt3)
        public decimal? RestNum { get; set; }        // 剩余可报损量 = (厂家+人为+其它) - 已审报损累计
    }

    /// <summary>一行可报损的仓库退料明细（导入弹窗用，小写属性 = JSON camelCase）。</summary>
    public class ProductLossReturnRow
    {
        public int returnBillId { get; set; }
        public int returnEntryId { get; set; }
        public string returnBillNo { get; set; }
        public int itemId { get; set; }
        public string cpbm { get; set; }
        public string productName { get; set; }
        public string spec { get; set; }
        public string unit { get; set; }
        public string categoryName { get; set; }
        public string batchNo { get; set; }
        public string warehouseName { get; set; }
        public decimal factoryNum { get; set; }     // 厂家原因 (FNumExt1)
        public decimal humanNum { get; set; }       // 人为原因 (FNumExt2)
        public decimal otherNum { get; set; }       // 其它原因 (FNumExt3)
        public decimal restNum { get; set; }        // 剩余可报损量 = (厂家+人为+其它) - 已审报损累计
        public decimal lossNum { get; set; }        // 默认导入的报损数量 = restNum
        public decimal batchStock { get; set; }
        public int stepId { get; set; }             // 来源退料行的工序（导入行继承）
        public string stepName { get; set; }
    }

    public class ProductLossEditViewModel
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime Date { get; set; }
        public int? DeptId { get; set; }
        public int? WorkShopId { get; set; }
        public int? ManagerId { get; set; }
        public string Remark { get; set; }
        public bool State { get; set; }
        public List<ProductLossRowInput> Rows { get; set; }
    }

    /// <summary>Flattened header+entry row for the list page (one row per entry).</summary>
    public class ProductLossListRow
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
        public string ReturnBillNo { get; set; }   // 来源仓库退料单号（手工行为空）

        /// <summary>数据闸门③-b：与服务端 W1 同源——明细未全部落在本仓 ⇒ 灰掉修改/审核/删除。</summary>
        public bool CanModify { get; set; } = true;
    }
}
