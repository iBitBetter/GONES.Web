using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 角色权限菜单树节点（带勾选状态）。
    /// </summary>
    public class RoleMenuNode
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public bool Checked { get; set; }
        public List<RoleMenuNode> Children { get; set; } = new List<RoleMenuNode>();
    }
}
