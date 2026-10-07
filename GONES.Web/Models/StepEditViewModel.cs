using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 工序名称编辑视图模型 —— 对应旧 WinForms BaseInfo.FrmStep 右侧编辑区。
    /// 字段映射 t_PMS_Step（FItemID/FName/FType/...）。
    /// </summary>
    public class StepEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "工序名称必须填写！")]
        [StringLength(100, ErrorMessage = "工序名称不能超过100个字符")]
        [Display(Name = "工序名称")]
        public string Name { get; set; }

        /// <summary>工序类型（字典 FParentID=5：自制工序/委外工序）</summary>
        public string Type { get; set; }

        /// <summary>工序算法（字典 FParentID=6：计时/计件）</summary>
        public string Algo { get; set; }

        /// <summary>标准工时（FHour）</summary>
        public decimal? Hour { get; set; }

        /// <summary>标准数量（FNum）</summary>
        public decimal? Num { get; set; }

        /// <summary>单件人工（FSingleArti，计件用）</summary>
        public decimal? SingleArti { get; set; }

        /// <summary>小时人工（FHourArti，计时用）</summary>
        public decimal? HourArti { get; set; }

        /// <summary>工序价格（FPrice）</summary>
        public decimal? Price { get; set; }

        /// <summary>工序单位（FWorkHourUnit，字典 FParentID=9）</summary>
        public string WorkHourUnit { get; set; }

        /// <summary>计价单位（FPriceUnit，字典 FParentID=9）</summary>
        public string PriceUnit { get; set; }

        /// <summary>固定损耗（FFixedLoss）</summary>
        public decimal? FixedLoss { get; set; }

        /// <summary>损耗标准值（FLossStandValue）</summary>
        public decimal? LossStandValue { get; set; }

        /// <summary>损耗率（FLossRate，旧系统按字符串存，如 "0%"）</summary>
        public string LossRate { get; set; }

        /// <summary>损耗值（FLossValue）</summary>
        public decimal? LossValue { get; set; }

        /// <summary>损耗单位（FLossUnit，字典 FParentID=9）</summary>
        public string LossUnit { get; set; }
    }
}
