using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 批次库存管理页（旧 WinForms Stock.FrmBatchStockManager / FrmBatchStockEdit）。
    /// 新「单据即账本」模型下，t_PMS_BatchNoStock 仅存单据主数据 + FBatchNum 缓存，
    /// 真实批次余额由 t_PMS_StockBill/Entry 按 FROB 聚合推导（StockService.BatchQty）。
    /// 因此本页只读：查询批次余额、对比「缓存」与「账本真实量」、对账重算(RecalcAll)、
    /// 双击批次看流水追溯。任何数量调整都必须经由出入库单据，不在本页手工改数。
    /// </summary>

    /// <summary>批次余额列表行：展示缓存量 vs 账本真实量及差异。</summary>
    public class BatchStockListRow
    {
        public string BatchNo { get; set; }          // FBatchNo 批号
        public int? ItemId { get; set; }             // FItemID
        public string Cpbm { get; set; }             // 商品代码
        public string Cpjc { get; set; }             // 商品名称
        public string Cpgg { get; set; }             // 规格型号
        public string Cplb { get; set; }             // 商品分类(lb)
        public string UnitName { get; set; }         // 单位
        public decimal CacheQty { get; set; }        // FBatchNum 缓存数量
        public decimal TruthQty { get; set; }        // 账本真实数量（聚合推导）
        public decimal Drift => CacheQty - TruthQty; // 差异（缓存 - 真实）
        public decimal? Price { get; set; }          // FPrice 单价
        public DateTime? BegDate { get; set; }       // FBegDate 入库日期
        public bool State { get; set; }              // FState 状态
        public int? BillType { get; set; }           // FBillType 单据类型
        public string BillTypeEx { get; set; }       // 单据类型名
    }

    /// <summary>批次流水追溯行（双击批次查看：构成该批次余额的出入库单据行）。</summary>
    public class BatchTraceRow
    {
        public string BillNo { get; set; }           // 单据编号
        public DateTime? Date { get; set; }          // 单据日期
        public string BillTypeEx { get; set; }       // 单据类型名
        public string RobEx { get; set; }            // 入/出（FROB=+1 入 / -1 出）
        public decimal Num { get; set; }             // FNum 数量
        public string ItemCode { get; set; }         // 商品代码
        public string ItemName { get; set; }         // 商品名称
        public string Note { get; set; }             // 备注
    }

    /// <summary>批次页汇总（当前筛选条件下）。</summary>
    public class BatchStockSummary
    {
        public decimal TotalCache { get; set; }      // 缓存合计
        public decimal TotalTruth { get; set; }      // 账本真实合计
        public int DriftCount { get; set; }          // 不一致批次数
    }
}
