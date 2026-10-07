using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 角色新建/编辑视图模型。
    /// </summary>
    public class RoleEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "角色名称不能为空")]
        [Display(Name = "角色名称")]
        public string RoleName { get; set; }

        [Display(Name = "角色备注")]
        public string RoleMemo { get; set; }

        [Display(Name = "只读角色（仅查询）")]
        public bool IsReadonly { get; set; }
    }
}
