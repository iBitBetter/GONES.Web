namespace GONES.Web.Models
{
    /// <summary>
    /// BOM 列表页的一行**明细**（= t_PMS_StepProductBom 一行 + 商品主档 t_ERP_ITEM 快照）。
    ///
    /// 列口径与旧 WinForms 分组网格（v_PMS_ProductRelBom）对齐：
    ///   产品代码 ← t_ERP_ITEM.ItemCode      名称 ← cpjc         规格型号 ← cpgg
    ///   商品分类 ← cplb                 单位 ← FBaseUnit（空则 wljbdw）
    ///   库存数量 ← FStockNum            存放仓库 ← ckid -> t_ERP_Department(IsDEP=1).DEPName
    ///   基本用量 ← FBaseNum             工序名称 ← FStepName    备注 ← FRemark
    ///   入库勾选 ← FIsProduct（= 产出/入库方向，与 BOM 表单的勾选同一列）
    /// </summary>
    public class BomDetailRow
    {
        public int ItemId { get; set; }

        public string Cpbm { get; set; }
        public string Cpjc { get; set; }
        public string Cpgg { get; set; }
        public string Cplb { get; set; }

        /// <summary>单位：优先取明细行 FBaseUnit，为空回退商品主档 wljbdw。</summary>
        public string Unit { get; set; }

        /// <summary>库存数量：t_ERP_ITEM.StockQuantity（展示口径唯一，见 MEMORY「库存展示一律 FStockNum」）。</summary>
        public decimal StockNum { get; set; }

        /// <summary>存放仓库名：ckid -> t_ERP_Department(IsDEP=1).DEPName（与 LoadWarehouseNames 同一口径）。</summary>
        public string WarehouseName { get; set; }

        /// <summary>基本用量。</summary>
        public decimal BaseNum { get; set; }

        public string StepName { get; set; }
        public string Remark { get; set; }

        /// <summary>入库（产出）方向：FIsProduct = 1。旧网格里就是那个勾选框。</summary>
        public bool IsProduct { get; set; }

        /// <summary>商品是否已停用（ty_zt 反转语义：1=启用）。停用行标红提示，但不隐藏——历史 BOM 要能看清。</summary>
        public bool Stopped { get; set; }
    }
}
