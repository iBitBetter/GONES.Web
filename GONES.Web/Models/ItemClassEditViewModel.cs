using System.ComponentModel.DataAnnotations;

namespace GONES.Web.Models
{
    /// <summary>
    /// 商品分类（t_ERP_ITEMCLASS）编辑模型。
    /// 对齐旧 WinForms FrmProductClassEdit：类别名称 + 上级类别 + 启用状态。
    /// 保存时由服务端推导 ParentClassCode/FullName/FullCode/ClassLevel（与旧逻辑一致）。
    /// </summary>
    public class ItemClassEditViewModel
    {
        public int ID { get; set; }

        /// <summary>类别名称（ClassName）。</summary>
        [Required(ErrorMessage = "类别名称不能为空。")]
        [StringLength(50)]
        [Display(Name = "类别名称")]
        public string Name { get; set; }

        /// <summary>上级类别 ID（FParentID），根节点(157)不可选。</summary>
        [Required(ErrorMessage = "请选择上级类别。")]
        [Display(Name = "上级类别")]
        public int? ParentId { get; set; }

        /// <summary>启用状态（UseStatus，1=启用）。</summary>
        [Display(Name = "启用")]
        public bool Enabled { get; set; } = true;
    }
}
