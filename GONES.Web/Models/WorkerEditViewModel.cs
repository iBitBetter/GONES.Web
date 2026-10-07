using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 人员管理（t_PMS_Worker）编辑模型。
    /// 对齐旧 WinForms FrmWorkerInfoEdit：姓名 / 所属部门(原车间, DEPID) / 手机 / 身份证 / 启用。
    /// </summary>
    public class WorkerEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "姓名不能为空")]
        [StringLength(500)]
        [Display(Name = "姓名")]
        public string Name { get; set; }

        /// <summary>所属部门，对应 t_ERP_Department.ID（原 t_PMS_Worker.WorkShopID 已改列 DEPID）。</summary>
        [Required(ErrorMessage = "请选择所属部门")]
        [Range(1, int.MaxValue, ErrorMessage = "请选择所属部门")]
        [Display(Name = "所属部门")]
        public int? DepID { get; set; }

        [StringLength(50)]
        [Display(Name = "手机号")]
        public string Mobile { get; set; }

        [StringLength(100)]
        [Display(Name = "身份证号")]
        public string IDNumber { get; set; }

        [Display(Name = "启用")]
        public bool Status { get; set; } = true;
    }
}
