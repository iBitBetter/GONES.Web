using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace GONES.Web.Models
{
    /// <summary>
    /// 生产产品收发存统计（旧 WinForms Report.FrmReceivedSendStockCount 的 Web 化，
    /// 菜单 t_ERP_Menu.ID=1313，ClassName=Report.FrmReceivedSendStockCount → /ProductInOutStock）。
    /// 列结构：商品主档(产品名称/规格型号/单位/产品分类) + 期初数量 + 结存数量 + 采购入库
    /// + 生产入库 + N 个工序列 + 存放仓库 + FItemID/FStockID。
    /// 工序列由 t_PMS_Step 动态驱动（按 FItemID 排序，过滤 FDelete=1 与 E2E_ 测试桩），
    /// 后续在工序表新增/删除工序无需改代码即可生效。
    /// 数量口径：(FNum+FNumExt1+FNumExt2+FNumExt3) 按单据类型分收/发；仅统计已审核单(FState=true)，与全站账本一致。
    /// </summary>
    public class ProductInOutStockRow
    {
        // 商品主档 + 仓库
        public string ItemName { get; set; }
        public string ItemModel { get; set; }
        public string ItemUnit { get; set; }
        public string ItemClass { get; set; }
        public string WarehouseName { get; set; }
        public int ItemId { get; set; }
        public int StockId { get; set; }

        // 固定收发列
        public decimal Opening { get; set; }     // 期初数量
        public decimal Closing { get; set; }     // 结存数量
        public decimal PurchaseIn { get; set; }  // 采购入库 (FB=0)
        public decimal ProdIn { get; set; }      // 生产入库 (FB=1)

        // 工序列：按 t_PMS_Step 动态顺序，下标 1..n 对应展示列（非数据库列，ADO.NET 读取后填充）
        [NotMapped]
        public decimal[] StepVals { get; set; }
        public decimal Step(int pos) => StepVals != null && pos >= 1 && pos < StepVals.Length ? StepVals[pos] : 0m;

        // 视图/导出按列布局预生成的单元格（与控制器 Layout 顺序一致，非数据库列）
        [NotMapped]
        public List<object> Cells { get; set; }
    }

    /// <summary>合计行：对所有商品行求和。</summary>
    public class ProductInOutStockSummary
    {
        public decimal Opening { get; set; }
        public decimal Closing { get; set; }
        public decimal PurchaseIn { get; set; }
        public decimal ProdIn { get; set; }
        [NotMapped]
        public decimal[] StepVals { get; set; }
        public decimal Step(int pos) => StepVals != null && pos >= 1 && pos < StepVals.Length ? StepVals[pos] : 0m;

        [NotMapped]
        public List<object> Cells { get; set; }
    }

    /// <summary>页面视图模型：列头 + 数据行 + 合计行。</summary>
    public class ProductInOutStockViewModel
    {
        public List<string> Columns { get; set; }
        /// <summary>与 Columns 等长的数值列标记（用于视图右对齐）。</summary>
        public List<bool> Numeric { get; set; }
        public List<ProductInOutStockRow> Rows { get; set; }
        public List<object> SummaryCells { get; set; }
    }
}
