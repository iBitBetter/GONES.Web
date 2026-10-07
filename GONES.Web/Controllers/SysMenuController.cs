using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;

using GONES.Web.Services;
using GONES.Web.Filters;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 系统菜单管理 —— 旧 WinForms SysManager.FrmSysMenu / FrmSysMenuEdit 的 Web 版，
    /// 绞杀者迁移第一个端到端模块。
    /// 布局复刻旧窗体：左侧主菜单树(ParentID==0)，右侧所选主菜单的直接子菜单表格。
    /// 仅管理员可访问（复用登录时写入的 IsAdmin claim）。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class SysMenuController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public SysMenuController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        /// <summary>
        /// 写操作要求管理员（RoleID 1..3）。菜单记录的 route 列直接决定路由跳转与入口闸门，
        /// 属授权基础设施——非管理员可改菜单即等于可改全员导航/越权入口。
        /// 类注释"仅管理员可访问"此前只落在文档上，这里补上服务端强制。读操作维持菜单闸门。
        /// </summary>
        private bool AmAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        private IActionResult DenyNonAdminWrite()
        {
            TempData["Error"] = "只有管理员才能修改菜单。";
            return RedirectToAction("Index", "Home");
        }

        // GET /SysMenu?pid=3
        public IActionResult Index(int? pid)
        {

            var roots = _db.t_ERP_Menu
                .Where(m => m.ParentID == 0 || m.ParentID == null)
                .OrderBy(m => m.ID).ToList();

            int currentId = pid ?? roots.FirstOrDefault()?.ID ?? 0;
            var children = _db.t_ERP_Menu
                .Where(m => m.ParentID == currentId)
                .OrderBy(m => m.ID).ToList();

            ViewBag.Roots = roots;
            ViewBag.CurrentId = currentId;
            return View(children);
        }

        // GET /SysMenu/Create?pid=3
        [HttpGet]
        public IActionResult Create(int? pid)
        {
            LoadRoots();
            return View("Form", new SysMenuEditViewModel { ParentID = pid ?? 0, Flag = true });
        }

        // GET /SysMenu/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var m = _db.t_ERP_Menu.FirstOrDefault(x => x.ID == id);
            if (m == null) return NotFound();
            LoadRoots();
            return View("Form", new SysMenuEditViewModel
            {
                ID = m.ID,
                ParentID = m.ParentID ?? 0,
                MenuName = m.MenuName,
                ClassName = m.ClassName,
                DllName = m.DllName,
                MenuType = m.MenuType ?? 0,
                OpenType = m.OpenType ?? 0,
                Flag = m.Flag,
                Memo = m.Memo,
            });
        }

        // POST /SysMenu/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(SysMenuEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();
            if (!ModelState.IsValid) { LoadRoots(); return View("Form", vm); }

            _db.t_ERP_Menu.Add(new t_ERP_Menu
            {
                ParentID = vm.ParentID,
                MenuName = vm.MenuName?.Trim(),
                ClassName = vm.ClassName?.Trim(),
                DllName = vm.DllName?.Trim(),
                MenuType = vm.MenuType,
                OpenType = vm.OpenType,
                Flag = vm.Flag,
                Memo = vm.Memo,
            });
            _db.SaveChanges();
            return RedirectToAction(nameof(Index), new { pid = vm.ParentID });
        }

        // POST /SysMenu/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, SysMenuEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();
            if (!ModelState.IsValid) { LoadRoots(); return View("Form", vm); }

            var m = _db.t_ERP_Menu.FirstOrDefault(x => x.ID == id);
            if (m == null) return NotFound();

            // 不允许把菜单挂到自己或其子孙之下：会造成菜单树成环。
            // 读取端 BuildTree 虽用 visited 兜底不崩进程，但环会让该分支显示错乱。
            if (vm.ParentID == id || IsDescendant(id, vm.ParentID))
            {
                ModelState.AddModelError(nameof(vm.ParentID), "上级菜单不能是自身或其下级菜单");
                LoadRoots();
                return View("Form", vm);
            }

            m.ParentID = vm.ParentID;
            m.MenuName = vm.MenuName?.Trim();
            m.ClassName = vm.ClassName?.Trim();
            m.DllName = vm.DllName?.Trim();
            m.MenuType = vm.MenuType;
            m.OpenType = vm.OpenType;
            m.Flag = vm.Flag;
            m.Memo = vm.Memo;
            _db.SaveChanges();
            return RedirectToAction(nameof(Index), new { pid = m.ParentID ?? 0 });
        }

        // POST /SysMenu/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            if (!AmAdmin) return DenyNonAdminWrite();
            var m = _db.t_ERP_Menu.FirstOrDefault(x => x.ID == id);
            if (m == null) return NotFound();

            // 有子菜单不允许删，先删子项（防误删整棵分支）。
            bool hasChildren = _db.t_ERP_Menu.Any(x => x.ParentID == id);
            if (hasChildren)
            {
                TempData["Error"] = "菜单[" + m.MenuName + "]下还有子菜单，请先删除子菜单。";
                return RedirectToAction(nameof(Index), new { pid = m.ParentID ?? 0 });
            }

            _db.t_ERP_Menu.Remove(m);
            _db.SaveChanges();
            return RedirectToAction(nameof(Index), new { pid = m.ParentID ?? 0 });
        }

        private void LoadRoots()
        {
            ViewBag.Roots = _db.t_ERP_Menu
                .Where(m => m.ParentID == 0 || m.ParentID == null)
                .OrderBy(m => m.ID).ToList();
        }

        /// <summary>判断 maybeDescendantId 是否为 ancestorId 的子孙（沿 ParentID 向上，visited 防环）。</summary>
        private bool IsDescendant(int ancestorId, int maybeDescendantId)
        {
            if (maybeDescendantId == 0 || maybeDescendantId == ancestorId) return false;
            var visited = new System.Collections.Generic.HashSet<int>();
            int cur = maybeDescendantId;
            while (cur != 0)
            {
                if (cur == ancestorId) return true;
                if (!visited.Add(cur)) break;                 // 数据已环：终止，避免死循环
                var node = _db.t_ERP_Menu.FirstOrDefault(x => x.ID == cur);
                if (node == null) break;
                cur = node.ParentID ?? 0;
            }
            return false;
        }
    }
}
