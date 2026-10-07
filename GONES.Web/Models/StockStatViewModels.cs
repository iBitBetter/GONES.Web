using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 生产进销存情况统计 —— 单行。列结构对齐旧 WinForms「生产进销存情况统计」报表：
    /// 批次号 / 产品代码 / 产品名称 / 规格型号 / 拼音码 / 单位 / 产品分类 / 期初数量 / 结存数量 /
    /// 采购入库 / 采购单价 / 采购入库金额 / 生产退料 / 生产入库 / 生产领料 / 换货入库 /
    /// 部门领料 / 部门退料 / 盘点数量 / 产品报损 / 存放仓库 / 产品代码 / 最后出库时间。
    /// 数量口径与 StockService 一致：(FNum+Ext1+Ext2+Ext3) 整体按 FROB 定向；仅统计已审核单(FState=1)。
    ///
    /// 本类同时承载【两个层级】的行（用户 2026-09-11 裁定：报表细化到批次）：
    ///   · 商品行 —— BatchNo=""，Batches = 该商品的批次明细（按批号）
    ///   · 批次行 —— BatchNo=批号，Batches=null
    /// 两层的列口径完全相同，只是批次行把聚合范围从 (商品) 收窄到 (商品, 批号)；
    /// 因此【商品行的每一列恒等于其批次行之和】——这是本报表的自洽不变量。
    /// </summary>
    public class StockStatRow
    {
        public int ItemId { get; set; }
        /// <summary>批次号。商品行为 ""（小计行）；批次行为 t_PMS_StockBillEntry.FBatchNo（空批号归一为 ""）。</summary>
        public string BatchNo { get; set; }
        public string Cpbm { get; set; }   // 产品代码
        public string Cpqc { get; set; }   // 产品名称
        public string Cpgg { get; set; }   // 规格型号
        public string Cppym { get; set; }  // 拼音码
        public string Wlbzdw { get; set; } // 单位
        public string Cplb { get; set; }   // 产品分类
        public string WarehouseName { get; set; } // 存放仓库

        public decimal Opening { get; set; }       // 期初数量
        public decimal Closing { get; set; }       // 结存数量（期末，账本口径）
        /// <summary>批次剩余 = Σ t_PMS_BatchNoStock.FBatchNum（批次账本缓存口径）。
        /// 与账面 Closing 并排展示（用户裁定 2026-09-11），两数不等即缓存漂移，页面红色高亮。</summary>
        public decimal? BatchClosing { get; set; }

        public decimal PurchaseInQty { get; set; }    // 采购入库
        public decimal PurchaseInAmount { get; set; } // 采购入库金额（**除税** Σ FAfterTaxAmount）
        /// <summary>采购单价（**除税**）：查询后由 ResolvePrice 在 C# 侧计算（期间除税均价，无采购时回落 RefPrice）。
        /// ⚠ EF Core SqlQueryRaw 要求结果集含全部映射列，而本列不由 SQL 返回 → 必须 [NotMapped]。</summary>
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public decimal? PurchasePrice { get; set; }
        /// <summary>参考单价 = **批次加权均价**（用户裁定 2026-09-11：报表与领料/退料/报损统一按批次口径）。
        /// Σ(t_PMS_BatchNoStock.FBatchNum × FPrice) ÷ Σ(FBatchNum)；批次价 FPrice 本身即除税价。
        /// 结存<=0 的批次不计入；无正结存批次时为 NULL。不读商品主档 ccdj。</summary>
        public decimal? RefPrice { get; set; }

        public decimal ProdReturnQty { get; set; } // 生产退料（FB=3）
        public decimal ProdInQty { get; set; }     // 生产入库（FB=1）
        public decimal ProdPickQty { get; set; }   // 生产领料（FB=8）
        public decimal ExchangeInQty { get; set; } // 换货入库（FB=6）
        public decimal DeptPickQty { get; set; }   // 部门领料（FB=9）
        public decimal DeptReturnQty { get; set; } // 部门退料（FB=2）
        public decimal StocktakeQty { get; set; }  // 盘点数量（盘盈FB=7 - 盘亏FB=10）
        public decimal LossQty { get; set; }       // 产品报损（FB=5）

        public DateTime? LastOut { get; set; }     // 最后出库时间

        /// <summary>仅商品行填充：该商品的批次明细行（批号级）。批次行此属性为 null。
        /// 只在控制器内存态装配，不参与 SQL 映射。</summary>
        public List<StockStatRow> Batches { get; set; }
    }

    /// <summary>合计行（对当前筛选条件下全部商品求和）。</summary>
    public class StockStatSummary
    {
        public decimal Opening { get; set; }
        public decimal Closing { get; set; }
        public decimal BatchClosing { get; set; }
        public decimal PurchaseInQty { get; set; }
        public decimal PurchaseInAmount { get; set; }
        public decimal ProdReturnQty { get; set; }
        public decimal ProdInQty { get; set; }
        public decimal ProdPickQty { get; set; }
        public decimal ExchangeInQty { get; set; }
        public decimal DeptPickQty { get; set; }
        public decimal DeptReturnQty { get; set; }
        public decimal StocktakeQty { get; set; }
        public decimal LossQty { get; set; }
    }
}
