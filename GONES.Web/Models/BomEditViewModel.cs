using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// BOM 单编辑模型。一个 BOM 单 = t_PMS_StepProductBom 中同一 FBomID 的一组明细行。
    /// </summary>
    public class BomEditViewModel
    {
        /// <summary>BOM 单号 FBomID（新建时服务端预生成 max+1）。</summary>
        public int BomId { get; set; }

        /// <summary>所属工序 FStepId（t_PMS_Step.FItemID）。</summary>
        [Display(Name = "工序")]
        [Range(1, int.MaxValue, ErrorMessage = "请选择工序。")]
        public int StepId { get; set; }

        /// <summary>明细行。</summary>
        public List<BomRowInput> Rows { get; set; } = new List<BomRowInput>();
    }
}
