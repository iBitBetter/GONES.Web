using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// Purchase in-stock bill (CGRKD) edit view model.
    /// Old WinForms: Stock.FrmPurchaseInStock. Tables: t_PMS_StockBill (FBillType=0)
    /// + t_PMS_StockBillEntry.
    /// Field mapping (from old form):
    ///   FDeptID=入库部门, FWorkShopID=入库车间(新系统取仓库 IsDEP=1), FManagerID=入库人(t_PMS_Worker),
    ///   FBuyingUnit=供货单位名称(t_ERP_DataDict FParentID=52), FInspectors=检验人(文本),
    ///   FCreaterID=制单人(登录用户), FModifyID/FModifyTime=修改人/时间, FState=审核状态(bit),
    ///   FROB=1 蓝字。明细: FNum=入库数量, FPrice=含税单价, FAmount=含税金额,
    ///   FTaxRate/FTaxRateValue=税率名/值(码表2), FTaxAmount=税额, FAfterTaxPrice/FAfterTaxAmount=税后,
    ///   FBatchNo 批号(审核时生成, 简化版不写批号库存表)。
    /// </summary>
    public class StockBillEditViewModel
    {
        public int InterId { get; set; }            // FInterID
        public string BillNo { get; set; }          // FBillNo (regenerated on save)
        public DateTime Date { get; set; }          // FDate
        public int? DeptId { get; set; }            // FDeptID   = t_ERP_Department
        public int? WorkShopId { get; set; }        // FWorkShopID = t_ERP_Department (IsDEP=1 仓库)
        public int? ManagerId { get; set; }         // FManagerID = t_PMS_Worker
        public string BuyingUnit { get; set; }      // FBuyingUnit = supplier name (dict 52)
        public string Inspectors { get; set; }      // FInspectors (free text)
        public string Remark { get; set; }          // FRemark
        public bool State { get; set; }             // FState: false=draft true=audited
        public string CreaterName { get; set; }     // display only
        public string ModifyName { get; set; }      // display only
        public DateTime? ModifyTime { get; set; }   // display only
        public List<StockBillRowInput> Rows { get; set; } = new List<StockBillRowInput>();
    }

    /// <summary>One entry row as posted from the form.</summary>
    public class StockBillRowInput
    {
        public int ItemId { get; set; }             // FItemID (t_ERP_ITEM.ID)
        public decimal? Qty { get; set; }           // FNum, must be > 0
        public decimal? Price { get; set; }         // FPrice (tax-included), must be >= 0
        public int TaxRateId { get; set; }          // t_ERP_DataDict FParentID=7 (13%/9%/6%/16%)
        public string Note { get; set; }            // FNote
        // Derived server-side on save: FAmount = Qty*Price; tax split from FTaxRateValue;
        // FBatchNo generated on audit; FStepID/FBomId = 0; FPlanNum etc = null.
    }

    /// <summary>
    /// Flat list row: one row per entry, header fields filled only on the first
    /// row of each bill (FirstOfBill) - same style as the old manager grid.
    /// </summary>
    public class StockBillListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        public string DeptName { get; set; }
        public string ManagerName { get; set; }
        public string BuyingUnit { get; set; }

        public int EntryId { get; set; }
        public string BatchNo { get; set; }         // FBatchNo
        public string Cpbm { get; set; }            // 产品代码
        public string Cplb { get; set; }            // 产品分类
        public string Cpjc { get; set; }            // 产品名称
        public string Cpgg { get; set; }            // 规格型号
        public string UnitName { get; set; }        // from item wljbdw
        public decimal? Qty { get; set; }           // FNum
        public decimal? Price { get; set; }         // FPrice
        public decimal? Amount { get; set; }        // FAmount
        public string TaxRate { get; set; }         // FTaxRate
        public string Note { get; set; }

        /// <summary>
        /// 仓库数据权限（W1）：整单明细未全部落在当前用户管理的仓库内 ⇒ false，
        /// 列表灰掉 修改/审核/删除/反审核。与服务端 DepotScope.BillFullyInScope 同源，
        /// 避免"按钮可点但提交被拒"或"按钮灰着却能构造 POST 绕过"。
        /// </summary>
        public bool CanModify { get; set; } = true;
    }
}
