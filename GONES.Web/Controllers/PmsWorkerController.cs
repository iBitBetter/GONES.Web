using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;

using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 人员管理 —— 旧 WinForms Public.FrmWorkerList / FrmWorkerInfoEdit 的 Web 版。
    /// 原为「车间人员设置」(t_PMS_Worker.WorkShopID 挂 t_PMS_WorkShop)，
    /// 车间管理已由 SYS-系统管理/部门管理替代，列 DEPID 对应 t_ERP_Department.ID。
    /// 布局：平铺人员列表（分页）+ 表单选部门；删除与旧系统一致为物理删除，
    /// 但增加引用检查（领用单 / 默认拣货员引用时拒绝删除）。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class PmsWorkerController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public PmsWorkerController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        // GET /PmsWorker（depId 为空 = 全部人员；选中部门时按该部门及其子部门过滤）
        public IActionResult Index(int? depId, int page = 1, int pageSize = 20)
        {

            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);

            var deps = LoadAll();

            // 选中部门：取该部门及全部子部门的 ID 集合，用于列表过滤
            HashSet<int> scope = null;
            if (depId.HasValue && deps.Any(d => d.ID == depId.Value))
                scope = CollectDescendants(deps, depId.Value);

            var query = _db.t_PMS_Worker.AsQueryable();
            if (scope != null)
                query = query.Where(w => scope.Contains(w.DEPID));
            query = query.OrderBy(w => w.ID);

            int total = query.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var list = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            ViewBag.DepNames = deps.ToDictionary(d => d.ID, d => d.DEPName);
            ViewBag.DepTree = BuildTree(deps);
            ViewBag.CurrentDepId = scope != null ? depId.Value : 0;
            ViewBag.CurrentDepName = scope != null
                ? deps.Where(d => d.ID == depId.Value).Select(d => d.DEPName).FirstOrDefault()
                : null;

            ViewBag.Pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index",
                ExtraValues = scope != null
                    ? new Dictionary<string, object> { ["depId"] = depId.Value }
                    : null
            };
            return View(list);
        }

        // GET /PmsWorker/Create
        [HttpGet]
        public IActionResult Create(int? depId)
        {
            BindDepOptions();
            // 从部门树进入新建时，预选该部门
            var vm = new WorkerEditViewModel { Status = true };
            if (depId.HasValue && _db.t_ERP_Department.Any(d => d.ID == depId.Value))
                vm.DepID = depId.Value;
            return View("Form", vm);
        }

        // POST /PmsWorker/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(WorkerEditViewModel vm)
        {

            Validate(vm, null);
            if (!ModelState.IsValid)
            {
                BindDepOptions();
                return View("Form", vm);
            }

            _db.t_PMS_Worker.Add(new t_PMS_Worker
            {
                FName = vm.Name?.Trim(),
                DEPID = vm.DepID ?? 0,
                FMobile = vm.Mobile?.Trim(),
                FIDNumber = vm.IDNumber?.Trim(),
                Status = vm.Status,
            });
            _db.SaveChanges();
            TempData["Success"] = "人员保存成功！";
            return RedirectToAction(nameof(Index));
        }

        // GET /PmsWorker/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var w = _db.t_PMS_Worker.FirstOrDefault(x => x.ID == id);
            if (w == null) return NotFound();
            BindDepOptions();
            return View("Form", new WorkerEditViewModel
            {
                ID = w.ID,
                Name = w.FName,
                DepID = w.DEPID,
                Mobile = w.FMobile,
                IDNumber = w.FIDNumber,
                Status = w.Status ?? true,
            });
        }

        // POST /PmsWorker/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, WorkerEditViewModel vm)
        {
            if (id != vm.ID) return BadRequest();

            var w = _db.t_PMS_Worker.FirstOrDefault(x => x.ID == id);
            if (w == null) return NotFound();

            Validate(vm, id);
            if (!ModelState.IsValid)
            {
                BindDepOptions();
                return View("Form", vm);
            }

            w.FName = vm.Name?.Trim();
            w.DEPID = vm.DepID ?? 0;
            w.FMobile = vm.Mobile?.Trim();
            w.FIDNumber = vm.IDNumber?.Trim();
            w.Status = vm.Status;
            _db.SaveChanges();
            TempData["Success"] = "人员修改成功。";
            return RedirectToAction(nameof(Index));
        }

        // POST /PmsWorker/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {

            var w = _db.t_PMS_Worker.FirstOrDefault(x => x.ID == id);
            if (w == null) return NotFound();

            var refMsg = CheckReferences(id);
            if (!string.IsNullOrEmpty(refMsg))
            {
                TempData["Error"] = refMsg;
                return RedirectToAction(nameof(Index));
            }

            _db.t_PMS_Worker.Remove(w);
            _db.SaveChanges();
            TempData["Success"] = "人员删除成功。";
            return RedirectToAction(nameof(Index));
        }

        #region 辅助

        private void Validate(WorkerEditViewModel vm, int? excludeId)
        {
            string name = vm.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                ModelState.AddModelError(nameof(vm.Name), "姓名不能为空。");

            if (!vm.DepID.HasValue || vm.DepID.Value <= 0)
                ModelState.AddModelError(nameof(vm.DepID), "请选择所属部门。");
            else if (!_db.t_ERP_Department.Any(d => d.ID == vm.DepID.Value))
                ModelState.AddModelError(nameof(vm.DepID), "所选部门不存在，请重新选择。");

            string idNo = vm.IDNumber?.Trim();
            if (!string.IsNullOrEmpty(idNo))
            {
                // 大陆身份证 18 位（末位可为 X）或 15 位旧号；宽松校验，仅拦截明显错误
                if (!(idNo.Length == 18 || idNo.Length == 15) || !idNo.All(char.IsLetterOrDigit))
                    ModelState.AddModelError(nameof(vm.IDNumber), "身份证号格式不正确（应为 15 或 18 位数字，末位可为 X）。");
            }
        }

        private string CheckReferences(int workerId)
        {
            if (_db.t_PMS_BillUse.Any(b => b.FWorkerID == workerId))
                return "该人员已被领用单引用，无法删除；请先处理相关领用单。";
            if (_db.t_PMS_DefaultPickerInfo.Any(p => p.FWorkerID == workerId))
                return "该人员已被设置为默认拣货员，无法删除；请先更换默认拣货员。";
            return null;
        }

        private List<t_ERP_Department> LoadAll()
            => _db.t_ERP_Department.OrderBy(d => d.ID).ToList();

        /// <summary>收集部门自身 + 全部子部门的 ID 集合（visited 防数据成环）。</summary>
        private static HashSet<int> CollectDescendants(List<t_ERP_Department> all, int rootId)
        {
            var set = new HashSet<int> { rootId };
            var queue = new Queue<int>();
            queue.Enqueue(rootId);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (var child in all.Where(d => d.ParentID == cur))
                    if (set.Add(child.ID))
                        queue.Enqueue(child.ID);
            }
            return set;
        }

        /// <summary>构建嵌套部门树（供左侧树形导航渲染，逻辑同部门管理）。</summary>
        private List<DepartmentNode> BuildTree(List<t_ERP_Department> all)
        {
            var result = new List<DepartmentNode>();
            var visited = new HashSet<int>();
            foreach (var root in all.Where(d => d.ParentID == 0 || d.ParentID == null))
                BuildNode(all, root, 0, visited, result);
            return result;
        }

        private static void BuildNode(List<t_ERP_Department> all, t_ERP_Department node,
                                      int depth, HashSet<int> visited, List<DepartmentNode> result)
        {
            if (!visited.Add(node.ID)) return;
            var dn = new DepartmentNode
            {
                Id = node.ID,
                Name = node.DEPName,
                ParentId = node.ParentID,
                IsDep = node.IsDEP,
                Enabled = node.Status == 1,
                Depth = depth,
            };
            result.Add(dn);
            foreach (var child in all.Where(d => d.ParentID == node.ID))
                BuildNode(all, child, depth + 1, visited, dn.Children);
        }

        /// <summary>部门下拉：按树形缩进展示全部部门（复用部门管理的树逻辑）。</summary>
        private void BindDepOptions()
        {
            var all = LoadAll();
            var options = new List<DepartmentNode>();
            var visited = new HashSet<int>();
            foreach (var root in all.Where(d => d.ParentID == 0 || d.ParentID == null))
                Flatten(all, root, 0, visited, options);
            ViewBag.DepOptions = options;
        }

        /// <summary>DFS 展开成缩进列表；visited 防数据成环时栈溢出。</summary>
        private static void Flatten(List<t_ERP_Department> all, t_ERP_Department node,
                                    int depth, HashSet<int> visited, List<DepartmentNode> result)
        {
            if (!visited.Add(node.ID)) return;
            result.Add(new DepartmentNode
            {
                Id = node.ID,
                Name = new string('　', depth) + (depth > 0 ? "└ " : "") + node.DEPName,
                IsDep = node.IsDEP,
                Enabled = node.Status == 1,
            });
            foreach (var child in all.Where(d => d.ParentID == node.ID))
                Flatten(all, child, depth + 1, visited, result);
        }

        #endregion
    }
}
