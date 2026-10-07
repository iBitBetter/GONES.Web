using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 系统菜单编辑表单（对应旧 WinForms SysManager.FrmSysMenuEdit）。
    /// 字段映射：ParentID=主菜单, MenuName=菜单名称, ClassName=启动类名,
    /// DllName=程序集名称, MenuType=窗体类型(0=分组菜单,1=MODU功能菜单),
    /// OpenType=打开方式(0=普通窗口,1=模态窗口), Flag=启用标记。
    /// 取值规则复刻自旧 FrmSysMenu.cs 的 CustomColumnDisplayText。
    /// </summary>
    public class SysMenuEditViewModel
    {
        public int ID { get; set; }

        [Required(ErrorMessage = "请选择主菜单")]
        public int ParentID { get; set; }

        [Required(ErrorMessage = "菜单名称不能为空")]
        [Display(Name = "菜单名称")]
        public string MenuName { get; set; }

        [Display(Name = "启动类名")]
        public string ClassName { get; set; }

        [Display(Name = "程序集名称")]
        public string DllName { get; set; }

        [Display(Name = "窗体类型")]
        public int MenuType { get; set; }

        [Display(Name = "打开方式")]
        public int OpenType { get; set; }

        [Display(Name = "启用")]
        public bool Flag { get; set; } = true;

        [Display(Name = "备注")]
        public string Memo { get; set; }
    }
}
