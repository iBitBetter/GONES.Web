using System;
using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 换货入库单 (HHRK) 编辑视图模型。
    /// 旧 WinForms：Stock.FrmPackingPrintingProblemInStockManager / FrmPackingPrintingProblemInStock
    /// (t_ERP_Menu.ID=1299「换货入库维护」)。
    /// 表头 t_PMS_StockBill (FBillType=6, 前缀 HHRK, FBillTypeEx=换货入库, FROB=+1)，
    /// 明细 t_PMS_StockBillEntry。
    /// 表头字段与采购入库单(FBillType=0)一致：
    ///   FDeptID=入库部门, FWorkShopID=入库车间, FManagerID=入库人(t_PMS_Worker),
    ///   FBuyingUnit=供货单位(码表 52), FInspectors=检验人, FCreaterID=制单人,
    ///   FModifyID/FModifyTime=修改人/时间, FState=审核状态, FROB=1 蓝字。
    /// 明细来源：已审核的仓库退料单(FBillType=3)，只取「厂家原因」退料数量 FNumExt1
    ///   （仓库退料四列口径：FNum=正常 / FNumExt1=厂家 / FNumExt2=人为 / FNumExt3=其它）。
    ///   来源行定位：FPickBillID=退料单 FInterID, FPickEntryID=退料明细 FEntryID,
    ///   FBillUseNo=退料单号（仅追溯展示）。换回的是新货，审核时按采购入库同规则新建批次
    ///   （StockService.ApplyInStock），因此 FBatchNo 保存时留空。
    /// </summary>
    public class ExchangeInEditViewModel
    {
        public int InterId { get; set; }            // FInterID
        public string BillNo { get; set; }          // FBillNo（保存时重新生成）
        public DateTime Date { get; set; }          // FDate
        public int? DeptId { get; set; }            // FDeptID   = t_ERP_Department（一级部门）
        public int? WorkShopId { get; set; }        // FWorkShopID = t_ERP_Department（IsDEP=2 车间）
        public int? ManagerId { get; set; }         // FManagerID = t_PMS_Worker
        public string BuyingUnit { get; set; }      // FBuyingUnit = 供货单位名称（码表 52）
        public string Inspectors { get; set; }      // FInspectors（文本）
        public string Remark { get; set; }          // FRemark
        public bool State { get; set; }             // FState: false=草稿 true=已审
        public string CreaterName { get; set; }     // 仅展示
        public string ModifyName { get; set; }      // 仅展示
        public DateTime? ModifyTime { get; set; }   // 仅展示
        public List<ExchangeInRowInput> Rows { get; set; } = new List<ExchangeInRowInput>();
    }

    /// <summary>
    /// 表单提交的一行明细。
    /// 服务端防篡改：只有 (ReturnBillId, ReturnEntryId) / Qty / Price / Note 被信任，
    /// 其余字段（商品、厂家退料量、剩余可换量、名称规格单位）一律由服务端按来源行重新推导。
    /// </summary>
    public class ExchangeInRowInput
    {
        public int? ReturnBillId { get; set; }      // 来源退料单 FInterID -> FPickBillID
        public int? ReturnEntryId { get; set; }     // 来源退料明细 FEntryID -> FPickEntryID
        public string ReturnBillNo { get; set; }    // 来源退料单号（展示 / FBillUseNo）
        public string SrcBatchNo { get; set; }      // 来源退料行的原批号（仅展示，不落库）

        public int ItemId { get; set; }             // FItemID（服务端推导）
        public string ProductName { get; set; }     // 展示
        public string Spec { get; set; }            // 展示
        public string CategoryName { get; set; }    // 展示
        public string Unit { get; set; }            // 展示

        public decimal? FactoryNum { get; set; }    // 来源行的厂家原因退料量 FNumExt1（服务端推导）
        public decimal? RestNum { get; set; }       // 剩余可换货量 = 厂家量 - 已审换货入库累计（服务端推导）
        public decimal? Qty { get; set; }           // 本次入库数量 -> FNum，必须 > 0 且 <= RestNum
        public decimal? Price { get; set; }         // FPrice，默认取来源退料行单价
        public string Note { get; set; }            // FNote
    }

    /// <summary>
    /// 列表行：一明细一行，表头字段只在每单第一行填充（FirstOfBill），与旧系统网格一致。
    /// </summary>
    public class ExchangeInListRow
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
        public string BatchNo { get; set; }         // FBatchNo（审核后生成）
        public string ReturnBillNo { get; set; }    // 来源退料单号（FBillUseNo）
        public string Cpbm { get; set; }            // 产品代码
        public string Cplb { get; set; }            // 产品分类
        public string Cpjc { get; set; }            // 产品名称
        public string Cpgg { get; set; }            // 规格型号
        public string UnitName { get; set; }        // 单位
        public decimal? Qty { get; set; }           // FNum
        public decimal? Price { get; set; }         // FPrice
        public decimal? Amount { get; set; }        // FAmount
        public string Note { get; set; }

        /// <summary>数据闸门③-b：与服务端 W1 同源——明细未全部落在本仓 ⇒ 灰掉修改/审核/删除。</summary>
        public bool CanModify { get; set; } = true;
    }

    /// <summary>「仓库退料单选择」模态里的一行候选（明细粒度）。</summary>
    public class ExchangeInReturnRow
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
        public string batchNo { get; set; }         // 退料行的原批号
        public string warehouseName { get; set; }
        public decimal factoryNum { get; set; }     // 厂家原因退料量
        public decimal restNum { get; set; }        // 剩余可换货量
        public decimal price { get; set; }
    }
}
