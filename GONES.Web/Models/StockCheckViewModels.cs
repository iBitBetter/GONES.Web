using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>One manual row of the stock-check (inventory count) bill (KCYPD).</summary>
    public class StockCheckRowInput
    {
        public int ItemId { get; set; }
        public string Cpbm { get; set; }
        public string ProductName { get; set; }
        public string Spec { get; set; }
        public string CategoryName { get; set; }
        public string Unit { get; set; }
        // 账面库存：只读展示（=当前 t_ERP_ITEM.StockQuantity），供盘点对照。不入库存计算。
        public decimal? BookStock { get; set; }
        // 盘点数量：录入的实际盘点量，审核时作为新批次入库量（FROB=1）。
        public decimal? CheckQty { get; set; }
        // 盘点单价：商品出厂单价 t_ERP_ITEM.FactoryPrice，新增商品时自动带出，审核时由后端强制取库值。
        public decimal? Price { get; set; }
        // 批号：建单时为空，审核后由 ApplyInStock 生成；也可手工指定已有批次。
        public string BatchNo { get; set; }
        public string WarehouseName { get; set; }
        public string Note { get; set; }
    }

    public class StockCheckEditViewModel
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime Date { get; set; }
        public int? DeptId { get; set; }
        // 盘点仓库（IsDEP=1 部门）：挂在盘点部门名下，供「部门→仓库→人员」级联。
        public int? WarehouseId { get; set; }
        public int? ManagerId { get; set; }
        public string Remark { get; set; }
        public bool State { get; set; }
        public List<StockCheckRowInput> Rows { get; set; }
    }

    /// <summary>Flattened header+entry row for the list page (one row per entry).</summary>
    public class StockCheckListRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public bool FirstOfBill { get; set; }
        public int EntryId { get; set; }
        public string DeptName { get; set; }
        public string WarehouseName { get; set; }
        public string ManagerName { get; set; }
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }
        public string Cplb { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string UnitName { get; set; }
        public decimal? BookStock { get; set; }
        public decimal? CheckQty { get; set; }
        public decimal? Price { get; set; }
        public string Note { get; set; }
    }

    /// <summary>盘点派生单（盘盈 FB=7 / 盘亏 FB=10）摘要 —— 挂在盘点单页上作只读入口。</summary>
    public class StockCheckDerivedRow
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public int BillType { get; set; }
        public string TypeName { get; set; }
        public DateTime? Date { get; set; }
        public decimal Qty { get; set; }
    }

    /// <summary>派生单明细行（只读）。</summary>
    public class StockCheckDerivedEntryRow
    {
        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string Unit { get; set; }
        public string BatchNo { get; set; }
        public decimal? Qty { get; set; }
        public decimal? Price { get; set; }
    }

    /// <summary>派生单只读视图模型（盘盈 / 盘亏共用）。</summary>
    public class StockCheckDerivedViewModel
    {
        public int InterId { get; set; }
        public string BillNo { get; set; }
        public int BillType { get; set; }
        public string TypeName { get; set; }
        public DateTime? Date { get; set; }
        public bool State { get; set; }
        public string Remark { get; set; }
        public string SourceBillNo { get; set; }
        public int? SourceInterId { get; set; }
        public List<StockCheckDerivedEntryRow> Rows { get; set; }
    }
}
