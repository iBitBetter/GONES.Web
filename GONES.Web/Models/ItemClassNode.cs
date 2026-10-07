using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;

namespace GONES.Web.Models
{
    /// <summary>
    /// 商品分类树节点，由 t_ERP_ITEMCLASS(FParentID 自引用) 构建，
    /// 供「商品分类」管理页与「商品管理」左侧分类树渲染。
    /// 最多 3 级：ClassLevel 1=根(所有分类)，2=大类，3=末级。
    /// </summary>
    public class ItemClassNode
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public bool Enabled { get; set; }
        public int Depth { get; set; }
        public List<ItemClassNode> Children { get; set; } = new List<ItemClassNode>();

        /// <summary>按 FParentID 构建分类树（根为 ClassLevel=1 的节点）。</summary>
        /// <summary>分类层级上限（ClassLevel 1=根，2=大类，3=末级）。递归时用它兜底，防脏数据成环。</summary>
        private const int MaxDepth = 32;

        public static List<ItemClassNode> BuildTree(List<t_ERP_ITEMCLASS> all)
        {
            var byParent = all.GroupBy(c => c.FParentID ?? 0)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.ID).ToList());
            List<ItemClassNode> Build(int parentId, int depth)
            {
                // Guard against a self/cyclic FParentID: without it a single bad row makes the
                // recursion endless (StackOverflow kills the whole process, uncatchable).
                if (depth > MaxDepth) return new List<ItemClassNode>();
                if (!byParent.TryGetValue(parentId, out var rows)) return new List<ItemClassNode>();
                return rows.Select(r => new ItemClassNode
                {
                    Id = r.ID,
                    Name = r.ClassName,
                    Level = r.ClassLevel ?? 1,
                    Enabled = (r.UseStatus ?? 1) == 1,
                    Depth = depth,
                    Children = Build(r.ID, depth + 1),
                }).ToList();
            }
            return Build(0, 0);
        }

        /// <summary>收集某分类及其全部子孙分类的 ID 集合（按部门过滤人员归类商品时同理）。</summary>
        public static HashSet<int> CollectDescendants(List<t_ERP_ITEMCLASS> all, int classId)
        {
            var ids = new HashSet<int> { classId };
            CollectInto(all, classId, ids, 0);
            return ids;
        }

        private static void CollectInto(List<t_ERP_ITEMCLASS> all, int classId,
                                        HashSet<int> ids, int depth)
        {
            // visited-check via `ids` + depth cap: both stop a cyclic FParentID from spinning
            // forever (StackOverflowException cannot be caught and would kill the worker).
            if (depth > MaxDepth) return;
            var children = all.Where(c => c.FParentID == classId).Select(c => c.ID).ToList();
            foreach (var cid in children)
            {
                if (!ids.Add(cid)) continue;   // already seen -> cycle, stop this branch
                CollectInto(all, cid, ids, depth + 1);
            }
        }

        /// <summary>构建父分类下拉选项：仅 ClassLevel&lt;=2 的节点（保证新增末级不超过 3 级），按树序缩进展示。</summary>
        public static List<SelectOption> BuildParentOptions(List<ItemClassNode> tree, int excludeId)
        {
            var options = new List<SelectOption>();
            void Walk(List<ItemClassNode> nodes, int depth)
            {
                foreach (var n in nodes)
                {
                    if (n.Id == excludeId) continue; // 不能把自己挂成自己的子级
                    options.Add(new SelectOption
                    {
                        Id = n.Id,
                        Text = new string('\u3000', depth) + n.Name,
                        Indent = depth,
                    });
                    Walk(n.Children, depth + 1);
                }
            }
            Walk(tree, 0);
            return options;
        }
    }

    /// <summary>下拉选项简单结构（分类选择等场景共用）。</summary>
    public class SelectOption
    {
        public int Id { get; set; }
        public string Text { get; set; }
        public int Indent { get; set; }
    }
}
