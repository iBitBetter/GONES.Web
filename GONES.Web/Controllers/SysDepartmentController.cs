using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;

using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 部门管理 —— 旧 WinForms SysManager.FrmDep / FrmDepEdit 的 Web 版。
    /// 布局复刻旧窗体：左侧"部门架构"树(ParentID==0 的"所有部门"为根)，
    /// 右侧所选部门的下级部门表格。仅管理员可访问。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class SysDepartmentController : Controller, IMenuGuarded
    {
        /// <summary>Process-wide lock for t_ERP_Department.ID (Max+1, not an identity column).</summary>
        private static readonly object DeptIdLock = new object();

        private readonly GonesPgDbContext _db;
        public SysDepartmentController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        /// <summary>
        /// 写操作要求管理员（与 SysRole / SysMenu 同范式）。
        /// t_ERP_Department 不只是"部门名称字典"：DepotScope 用它判定哪些 ID 是仓库
        /// （IsDEP=1），单据的 FWorkShopID 也按它解析车间/仓库名。非管理员一旦能增删改，
        /// 就能改动别人的仓库归属判定基础数据，故必须与菜单闸门分开、单独要求 admin。
        /// </summary>
        private bool AmAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        private IActionResult DenyNonAdminWrite()
        {
            TempData["Error"] = "\u53ea\u6709\u7ba1\u7406\u5458\u624d\u80fd\u4fee\u6539\u7ec4\u7ec7\u67b6\u6784\u3002";   // 只有管理员才能修改组织架构。
            return RedirectToAction(nameof(Index));
        }

        // GET /SysDepartment?pid=1
        public IActionResult Index(int? pid)
        {

            var all = LoadAll();
            var roots = all.Where(d => d.ParentID == 0 || d.ParentID == null)
                           .OrderBy(d => d.ID).ToList();

            int currentId = pid ?? roots.FirstOrDefault()?.ID ?? 0;

            var children = all.Where(d => d.ParentID == currentId)
                              .OrderBy(d => d.ID).ToList();

            ViewBag.Tree = BuildTree(all);
            ViewBag.CurrentId = currentId;
            ViewBag.Path = BuildPath(all, currentId);
            ViewBag.DepTreeBaseUrl = "/SysDepartment";
            ViewBag.DepTreeParam = "pid";
            ViewBag.CurrentDepId = currentId;
            return View(children);
        }

        // GET /SysDepartment/Create?pid=1
        [HttpGet]
        public IActionResult Create(int? pid)
        {
            LoadParentOptions();
            return View("Form", new DepartmentEditViewModel { ParentID = pid ?? 0, Status = true });
        }

        // GET /SysDepartment/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var d = _db.t_ERP_Department.FirstOrDefault(x => x.ID == id);
            if (d == null) return NotFound();
            LoadParentOptions(id);
            return View("Form", new DepartmentEditViewModel
            {
                ID = d.ID,
                ParentID = d.ParentID ?? 0,
                DEPName = d.DEPName,
                IsDEP = d.IsDEP,
                Status = d.Status == 1,
            });
        }

        // POST /SysDepartment/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(DepartmentEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            string name = vm.DEPName?.Trim();
            if (string.IsNullOrEmpty(name))
                ModelState.AddModelError(nameof(vm.DEPName), "部门名称不能为空");
            else if (_db.t_ERP_Department.Any(d => d.ParentID == vm.ParentID && d.DEPName == name))
                ModelState.AddModelError(nameof(vm.DEPName), "该上级部门下已存在同名部门");

            if (vm.ParentID == 0)
                ModelState.AddModelError(nameof(vm.ParentID), "请选择上级部门");
            else if (!_db.t_ERP_Department.Any(d => d.ID == vm.ParentID))
                ModelState.AddModelError(nameof(vm.ParentID), "所选上级部门不存在，请重新选择。");

            if (!ModelState.IsValid) { LoadParentOptions(); return View("Form", vm); }

            // t_ERP_Department.ID 不是自增列，必须手动分配（与旧 WinForms 一致）。
            // "read max -> add -> SaveChanges" must hold a process-wide lock, otherwise two
            // concurrent creates compute the same id and the second INSERT fails on the PK.
            int newId;
            lock (DeptIdLock)
            {
                newId = (_db.t_ERP_Department.Max(x => (int?)x.ID) ?? 0) + 1;
                _db.t_ERP_Department.Add(new t_ERP_Department
                {
                    ID = newId,
                    ParentID = vm.ParentID,
                    DEPName = name,
                    DEPLevel = 1,          // 与旧 FrmDepEdit 一致：根下部门/车间固定为 1
                    IsDEP = vm.IsDEP,
                    Status = vm.Status ? 1 : 0,
                });
                _db.SaveChanges();
            }
            return RedirectToAction(nameof(Index), new { pid = vm.ParentID });
        }

        // POST /SysDepartment/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, DepartmentEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            var d = _db.t_ERP_Department.FirstOrDefault(x => x.ID == id);
            if (d == null) return NotFound();

            string name = vm.DEPName?.Trim();
            if (string.IsNullOrEmpty(name))
                ModelState.AddModelError(nameof(vm.DEPName), "部门名称不能为空");
            else if (_db.t_ERP_Department.Any(x => x.ID != id && x.ParentID == vm.ParentID && x.DEPName == name))
                ModelState.AddModelError(nameof(vm.DEPName), "该上级部门下已存在同名部门");

            // 上级部门必须真实存在（ParentID=0 表示保持根级，沿用旧行为不拦）
            if (vm.ParentID != 0 && !_db.t_ERP_Department.Any(d => d.ID == vm.ParentID))
                ModelState.AddModelError(nameof(vm.ParentID), "所选上级部门不存在，请重新选择。");

            // 不允许把部门挂到自己或其子孙之下（会造成树断裂）
            if (vm.ParentID == id || IsDescendant(id, vm.ParentID))
                ModelState.AddModelError(nameof(vm.ParentID), "上级部门不能是自身或其下级部门");

            if (!ModelState.IsValid) { LoadParentOptions(id); return View("Form", vm); }

            d.ParentID = vm.ParentID;
            d.DEPName = name;
            d.IsDEP = vm.IsDEP;
            d.Status = vm.Status ? 1 : 0;
            _db.SaveChanges();
            return RedirectToAction(nameof(Index), new { pid = d.ParentID ?? 0 });
        }

        // POST /SysDepartment/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            if (!AmAdmin) return DenyNonAdminWrite();
            var d = _db.t_ERP_Department.FirstOrDefault(x => x.ID == id);
            if (d == null) return NotFound();

            int parentId = d.ParentID ?? 0;

            if (id == 1 || parentId == 0)
            {
                TempData["Error"] = "根部门不允许删除。";
                return RedirectToAction(nameof(Index), new { pid = parentId });
            }

            if (_db.t_ERP_Department.Any(x => x.ParentID == id))
            {
                TempData["Error"] = "部门[" + d.DEPName + "]下还有下级部门/车间，请先删除下级。";
                return RedirectToAction(nameof(Index), new { pid = parentId });
            }

            int userCount = _db.t_ERP_UserInfo.Count(u => u.DEPID == id);
            if (userCount > 0)
            {
                TempData["Error"] = "部门[" + d.DEPName + "]下还有 " + userCount + " 个用户，请先调整用户所属部门。";
                return RedirectToAction(nameof(Index), new { pid = parentId });
            }

            // 员工(t_PMS_Worker.DEPID) 与 商品仓库(t_ERP_ITEM.WarehouseId) 也指向部门 ID。
            // 不拦住就会留下孤儿外键：商品仓库名解析成空白、盘点/退料的“人员下拉”
            // 因 w.DEPID == scope 过滤而变空，单据直接建不出来。
            int workerCount = _db.t_PMS_Worker.Count(w => w.DEPID == id);
            if (workerCount > 0)
            {
                TempData["Error"] = "部门[" + d.DEPName + "]下还有 " + workerCount + " 个员工，请先调整员工所属部门。";
                return RedirectToAction(nameof(Index), new { pid = parentId });
            }

            int itemCount = _db.t_ERP_ITEM.Count(i => i.WarehouseId == id);
            if (itemCount > 0)
            {
                TempData["Error"] = "部门[" + d.DEPName + "]仍被 " + itemCount + " 个商品作为仓库使用，请先修改这些商品的仓库。";
                return RedirectToAction(nameof(Index), new { pid = parentId });
            }

            _db.t_ERP_Department.Remove(d);
            _db.SaveChanges();
            return RedirectToAction(nameof(Index), new { pid = parentId });
        }

        #region 辅助

        private List<t_ERP_Department> LoadAll()
            => _db.t_ERP_Department.OrderBy(d => d.ID).ToList();

        /// <summary>上级部门下拉：按树形缩进展示全部部门（编辑时排除自身及子孙）。</summary>
        private void LoadParentOptions(int? excludeId = null)
        {
            var all = LoadAll();
            var banned = new HashSet<int>();
            if (excludeId.HasValue)
            {
                banned.Add(excludeId.Value);
                CollectDescendants(all, excludeId.Value, banned);
            }

            var options = new List<DepartmentNode>();
            var visited = new HashSet<int>();
            foreach (var root in all.Where(d => d.ParentID == 0 || d.ParentID == null))
                Flatten(all, root, 0, banned, visited, options);

            ViewBag.ParentOptions = options;
        }

        /// <summary>DFS 展开成缩进列表。<paramref name="visited"/> 防止数据出现环(如 0↔1)时栈溢出。</summary>
        private static void Flatten(List<t_ERP_Department> all, t_ERP_Department node,
                                    int depth, HashSet<int> banned, HashSet<int> visited, List<DepartmentNode> result)
        {
            if (banned.Contains(node.ID)) return;
            if (!visited.Add(node.ID)) return;
            result.Add(new DepartmentNode
            {
                Id = node.ID,
                Name = new string('　', depth) + (depth > 0 ? "└ " : "") + node.DEPName,
                IsDep = node.IsDEP,
                Enabled = node.Status == 1,
            });
            foreach (var child in all.Where(d => d.ParentID == node.ID))
                Flatten(all, child, depth + 1, banned, visited, result);
        }

        private List<DepartmentNode> BuildTree(List<t_ERP_Department> all)
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

        private static void SetDepth(List<DepartmentNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                n.Depth = depth;
                n.ChildCount = n.Children.Count;
                SetDepth(n.Children, depth + 1);
            }
        }

        /// <summary>面包屑：从根到当前部门的路径。</summary>
        private List<t_ERP_Department> BuildPath(List<t_ERP_Department> all, int id)
        {
            var path = new List<t_ERP_Department>();
            var map = all.ToDictionary(d => d.ID);
            int? cur = id;
            var guard = new HashSet<int>();
            while (cur.HasValue && map.ContainsKey(cur.Value) && guard.Add(cur.Value))
            {
                var node = map[cur.Value];
                path.Insert(0, node);
                cur = (node.ParentID.HasValue && node.ParentID.Value != 0) ? node.ParentID : null;
            }
            return path;
        }

        private bool IsDescendant(int ancestorId, int nodeId)
        {
            var all = LoadAll();
            var banned = new HashSet<int>();
            CollectDescendants(all, ancestorId, banned);
            return banned.Contains(nodeId);
        }

        private static void CollectDescendants(List<t_ERP_Department> all, int id, HashSet<int> banned)
        {
            foreach (var child in all.Where(d => d.ParentID == id))
                if (banned.Add(child.ID))
                    CollectDescendants(all, child.ID, banned);
        }

        #endregion
    }
}
