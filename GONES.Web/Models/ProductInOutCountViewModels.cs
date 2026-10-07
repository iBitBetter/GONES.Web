using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 产品入出库统计 —— 单据明细行（复刻旧 WinForms Report.FrmInOutCount 的明细视图，
    /// 菜单 t_ERP_Menu.ID=1315，ClassName=Report.FrmInOutCount → /ProductInOutCount）。
    /// 一张单据(t_PMS_StockBill)含多行明细(t_PMS_StockBillEntry)，本行即一条明细。
    /// </summary>
    public class ProductInOutCountRow
    {
        public string FBillNo { get; set; }
        public string FBillTypeEx { get; set; }
        public DateTime? FDate { get; set; }
        public string WarehouseName { get; set; }   // 仓库 = t_ERP_ITEM.WarehouseId → t_ERP_Department(IsDEP=1).DEPName
        public string DeptName { get; set; }        // 部门/车间 = t_PMS_StockBill.FWorkShopID → t_ERP_Department.DEPName
        public string ItemName { get; set; }        // 商品名称 t_ERP_ITEM.ItemFullName
        public string ItemNumber { get; set; }      // 商品编码 t_ERP_ITEM.ItemCode
        public string ItemModel { get; set; }       // 规格型号 t_ERP_ITEM.ItemSpec
        public string ItemUnit { get; set; }        // 单位 t_ERP_ITEM.PackageUnit
        public string ItemClass { get; set; }       // 产品分类 t_ERP_ITEM.ProductCategory
        public string StepName { get; set; }        // 工序 t_PMS_Step.FName
        public string CreaterName { get; set; }     // 制单人 t_ERP_UserInfo.UserName
        public string FBatchNo { get; set; }
        public string FRemark { get; set; }
        public decimal FNum { get; set; }
        public decimal FNumExt1 { get; set; }
        public decimal FNumExt2 { get; set; }
        public decimal FNumExt3 { get; set; }
        public decimal FPrice { get; set; }
        public decimal FAmount { get; set; }
    }

    /// <summary>按某一维度(单据类型/仓库/商品)的汇总小计行。</summary>
    public class ProductInOutCountSubtotal
    {
        /// <summary>分组显示名（单据类型名 / 仓库名 / 商品名）。</summary>
        public string GroupName { get; set; }
        public int Count { get; set; }
        public decimal FNum { get; set; }
        public decimal FNumExt1 { get; set; }
        public decimal FNumExt2 { get; set; }
        public decimal FNumExt3 { get; set; }
        public decimal FAmount { get; set; }
    }

    /// <summary>统计汇总信息：合计 + 按单据类型/仓库/商品三套小计（二次分组）。</summary>
    public class ProductInOutCountSummary
    {
        public int Count { get; set; }
        public decimal FNum { get; set; }
        public decimal FNumExt1 { get; set; }
        public decimal FNumExt2 { get; set; }
        public decimal FNumExt3 { get; set; }
        public decimal FAmount { get; set; }
        public List<ProductInOutCountSubtotal> ByType { get; set; } = new List<ProductInOutCountSubtotal>();
        public List<ProductInOutCountSubtotal> ByWarehouse { get; set; } = new List<ProductInOutCountSubtotal>();
        public List<ProductInOutCountSubtotal> ByProduct { get; set; } = new List<ProductInOutCountSubtotal>();
    }

    public class ProductInOutCountViewModel
    {
        /// <summary>当前方向：in=入库单 / out=出库单。</summary>
        public string Direction { get; set; }
        public List<ProductInOutCountRow> Rows { get; set; } = new List<ProductInOutCountRow>();
        public ProductInOutCountSummary Summary { get; set; } = new ProductInOutCountSummary();
        /// <summary>当前方向可选的单据类型(FBillType,显示名)。</summary>
        public List<(int FBillType, string Name)> BillTypeOptions { get; set; } = new List<(int, string)>();
    }
}
