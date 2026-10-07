using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// BOM 列表页一行 = 一个 BOM 单（按 FBomID 聚合）。
    /// 既作**分组标题**（单号/产出品/明细数/状态），也作**明细容器**（Details）。
    /// </summary>
    public class BomSummary
    {
        public int BomId { get; set; }
        public int? StepId { get; set; }
        public string StepName { get; set; }

        /// <summary>明细行数。</summary>
        public int ItemCount { get; set; }

        /// <summary>该单的明细行（按 FID 顺序），列表页展开成子行显示。</summary>
        public List<BomDetailRow> Details { get; set; } = new List<BomDetailRow>();

        /// <summary>入库（产出）方向行数 / 出库（投入）方向行数。</summary>
        public int InCount { get; set; }
        public int OutCount { get; set; }

        /// <summary>产出品（FIsProduct=1 的那一行）的商品编码/名称/类别。</summary>
        public string OutputCpbm { get; set; }
        public string OutputName { get; set; }
        public string OutputClass { get; set; }

        /// <summary>产出品是否已停用（用于标红提示）。</summary>
        public bool OutputStopped { get; set; }

        /// <summary>
        /// 整单是否已禁用（t_PMS_StepProductBom.FDelete=1）。
        /// 禁用的 BOM 不再出现在要货/生产的选择列表中，但历史单据仍可正常追溯。
        /// </summary>
        public bool Disabled { get; set; }
    }
}
