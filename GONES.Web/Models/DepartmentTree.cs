using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;

namespace GONES.Web.Models
{
    /// <summary>
    /// 部门自引用树构建助手。供"部门管理"与"用户管理"左侧"部门架构"树状菜单复用，
    /// 并供用户管理按部门（含子部门）筛选用户。逻辑与 SysDepartmentController 原 BuildTree 一致。
    /// </summary>
    public static class DepartmentTree
    {
        /// <summary>由扁平部门列表构建层级 DepartmentNode 树（roots = ParentID 为 0/空 的节点）。</summary>
        public static List<DepartmentNode> Build(List<t_ERP_Department> all)
        {
            var nodes = all.ToDictionary(
                d => d.ID,
                d => new DepartmentNode
                {
                    Id = d.ID,
                    Name = d.DEPName,
                    ParentId = d.ParentID,
                    IsDep = d.IsDEP,
                    Enabled = d.Status == 1,
                });

            var roots = new List<DepartmentNode>();
            foreach (var node in nodes.Values)
            {
                bool hasParent = node.ParentId.HasValue
                                 && node.ParentId.Value != 0
                                 && nodes.ContainsKey(node.ParentId.Value);
                if (hasParent)
                    nodes[node.ParentId.Value].Children.Add(node);
                else
                    roots.Add(node);
            }
            SetDepth(roots, 0);
            return roots;
        }

        /// <summary>收集 rootId 自身 + 全部后代部门的 ID 集合，供用户管理按部门（含子部门）筛选。</summary>
        public static HashSet<int> CollectSubtreeIds(List<t_ERP_Department> all, int rootId)
        {
            var set = new HashSet<int> { rootId };
            foreach (var c in all.Where(d => d.ParentID == rootId))
                if (set.Add(c.ID)) CollectSubtreeIds(all, c.ID, set);
            return set;
        }

        private static void CollectSubtreeIds(List<t_ERP_Department> all, int id, HashSet<int> set)
        {
            foreach (var c in all.Where(d => d.ParentID == id))
                if (set.Add(c.ID)) CollectSubtreeIds(all, c.ID, set);
        }

        private static void SetDepth(List<DepartmentNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                n.Depth = depth;
                n.ChildCount = n.Children.Count;
                SetDepth(n.Children, depth + 1);
            }
        }
    }
}
