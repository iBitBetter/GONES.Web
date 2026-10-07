using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 菜单树节点，由 t_ERP_Menu(ParentID 自引用) 构建。
    /// </summary>
    public class MenuNode
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public string Url { get; set; }
        public List<MenuNode> Children { get; set; } = new List<MenuNode>();
    }
}
