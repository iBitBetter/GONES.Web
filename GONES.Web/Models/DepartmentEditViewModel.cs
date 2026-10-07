using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 部门编辑表单（对应旧 WinForms SysManager.FrmDepEdit）。
    /// 字段映射：ParentID=上级部门, DEPName=部门名称,
    /// IsDEP=部门类型(0=正常部门/1=仓库/2=车间/3=班组), Status=启用(旧窗体 chkEnable)。
    /// DEPLevel 沿用旧逻辑固定为 1（根目录"所有部门"为 0，其下部门/仓库/车间均为 1）。
    /// </summary>
    public class DepartmentEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "请选择上级部门")]
        [Display(Name = "上级部门")]
        public int ParentID { get; set; }

        [Required(ErrorMessage = "部门名称不能为空")]
        [Display(Name = "部门名称")]
        public string DEPName { get; set; }

        [Display(Name = "部门类型")]
        [Range(0, 3, ErrorMessage = "部门类型只能是 0=正常部门/1=仓库/2=车间/3=班组")]
        public byte IsDEP { get; set; }

        [Display(Name = "启用")]
        public bool Status { get; set; } = true;
    }
}
