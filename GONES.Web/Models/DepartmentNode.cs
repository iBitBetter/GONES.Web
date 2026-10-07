using System.Collections.Generic;

namespace GONES.Web.Models
{
    /// <summary>
    /// 部门树节点，由 t_ERP_Department(ParentID 自引用) 构建，供左侧"部门架构"树渲染。
    /// IsDep 对应 t_ERP_Department.IsDEP：0=正常部门/1=仓库/2=车间。
    /// </summary>
    public class DepartmentNode
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? ParentId { get; set; }
        public byte IsDep { get; set; }
        public bool Enabled { get; set; }
        public int Depth { get; set; }
        public int ChildCount { get; set; }
        public List<DepartmentNode> Children { get; set; } = new List<DepartmentNode>();
    }
}
