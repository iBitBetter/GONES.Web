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
    /// 角色管理 —— 复刻旧 WinForms 的 SysManager.FrmRoleList / FrmRoleEdit。
    /// 左侧角色列表，右上「功能权限列表」菜单树，右下「用户列表」。
    /// 仅管理员可访问。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class SysRoleController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public SysRoleController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        /// <summary>
        /// 写操作要求管理员（RoleID 1..3，登录时写入的 IsAdmin claim）。
        /// 菜单授权（CanAccessRoute）只回答"能不能进这个页面"；而角色的功能权限矩阵本身就是
        /// 授权体系——非管理员一旦拿到菜单 18，就能给自己的角色勾上任意菜单，等效提权。
        /// 读操作（Index/表单页）维持菜单闸门不变。
        /// </summary>
        private bool AmAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        private IActionResult DenyNonAdminWrite()
        {
            TempData["Error"] = "只有管理员才能修改角色及权限。";
            return RedirectToAction(nameof(Index));
        }

        // GET /SysRole?id=3
        public IActionResult Index(int? id)
        {

            var roles = _db.t_ERP_Role.OrderBy(r => r.ID).ToList();
            int selectedId = id ?? roles.FirstOrDefault()?.ID ?? 0;

            ViewBag.Roles = roles;
            ViewBag.SelectedId = selectedId;
            ViewBag.MenuTree = BuildMenuTree(selectedId);
            ViewBag.Users = selectedId == 0
                ? new List<t_ERP_UserInfo>()
                : _db.t_ERP_UserInfo.Where(u => u.RoleID == selectedId && u.Status == 1)
                                   .OrderBy(u => u.ID).ToList();

            return View();
        }

        // GET /SysRole/Create
        [HttpGet]
        public IActionResult Create()
        {
            return View("Form", new RoleEditViewModel());
        }

        // POST /SysRole/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(RoleEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            if (string.IsNullOrWhiteSpace(vm.RoleName))
                ModelState.AddModelError(nameof(vm.RoleName), "角色名称不能为空");

            if (!ModelState.IsValid) return View("Form", vm);

            _db.t_ERP_Role.Add(new t_ERP_Role
            {
                RoleName = vm.RoleName.Trim(),
                RoleMemo = vm.RoleMemo?.Trim() ?? "",
                Iselect = 0,
                IsReadonly = (byte)(vm.IsReadonly ? 1 : 0)
            });
            _db.SaveChanges();
            return RedirectToAction(nameof(Index));
        }

        // GET /SysRole/Edit/3
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var role = _db.t_ERP_Role.FirstOrDefault(r => r.ID == id);
            if (role == null) return NotFound();
            return View("Form", new RoleEditViewModel
            {
                ID = role.ID,
                RoleName = role.RoleName,
                RoleMemo = role.RoleMemo,
                IsReadonly = role.IsReadonly == 1
            });
        }

        // POST /SysRole/Edit/3
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, RoleEditViewModel vm)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            var role = _db.t_ERP_Role.FirstOrDefault(r => r.ID == id);
            if (role == null) return NotFound();

            if (string.IsNullOrWhiteSpace(vm.RoleName))
                ModelState.AddModelError(nameof(vm.RoleName), "角色名称不能为空");

            if (!ModelState.IsValid) return View("Form", vm);

            role.RoleName = vm.RoleName.Trim();
            role.RoleMemo = vm.RoleMemo?.Trim() ?? "";
            role.IsReadonly = (byte)(vm.IsReadonly ? 1 : 0);
            _db.SaveChanges();
            return RedirectToAction(nameof(Index), new { id = role.ID });
        }

        // POST /SysRole/SavePermission
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SavePermission(int roleId, int[] menuIds)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            var role = _db.t_ERP_Role.FirstOrDefault(r => r.ID == roleId);
            if (role == null) return NotFound();

            string ids = menuIds == null ? "" : string.Join(";", menuIds.Distinct());

            // 归一化写入：先删掉该角色的**全部**行，再只写一行。
            // t_ERP_RoleMenu 历史上允许一个角色多行，而权限判定（MenuService.RoleMenuIds）
            // 会**聚合所有行** —— 若只改第一行、把其余行留下，管理员以为收回了权限，
            // 旧的授权行仍在生效（静默权限泄漏）。这里强制维持「一角色一行」不变量。
            var existing = _db.t_ERP_RoleMenu.Where(r => r.RoleID == roleId).ToList();
            foreach (var rm in existing) _db.t_ERP_RoleMenu.Remove(rm);
            _db.t_ERP_RoleMenu.Add(new t_ERP_RoleMenu
            {
                RoleID = roleId,
                MenuID = ids
            });
            _db.SaveChanges();

            TempData["Success"] = "角色「" + role.RoleName + "」的功能权限已保存。";
            return RedirectToAction(nameof(Index), new { id = roleId });
        }

        // POST /SysRole/Delete/3
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            if (!AmAdmin) return DenyNonAdminWrite();

            var role = _db.t_ERP_Role.FirstOrDefault(r => r.ID == id);
            if (role == null) return NotFound();

            // Count ALL users, not just the enabled ones: deleting the role of a disabled user
            // leaves a dangling RoleID that silently becomes admin-ish once re-enabled.
            int userCount = _db.t_ERP_UserInfo.Count(u => u.RoleID == id);
            if (userCount > 0)
            {
                TempData["Error"] = "角色「" + role.RoleName + "」下还有 " + userCount + " 个启用用户，请先调整用户角色。";
                return RedirectToAction(nameof(Index), new { id = id });
            }

            // 同样按「全部行」删除：多行角色下只删 FirstOrDefault 会留下孤儿授权行，
            // 一旦该 RoleID 被复用，孤儿行会变成幽灵授权。
            var roleMenus = _db.t_ERP_RoleMenu.Where(r => r.RoleID == id).ToList();
            foreach (var rm in roleMenus) _db.t_ERP_RoleMenu.Remove(rm);
            _db.t_ERP_Role.Remove(role);
            _db.SaveChanges();

            TempData["Success"] = "角色「" + role.RoleName + "」已删除。";
            return RedirectToAction(nameof(Index));
        }

        #region 辅助

        private List<RoleMenuNode> BuildMenuTree(int roleId)
        {
            var checkedIds = new HashSet<int>();
            if (roleId > 0)
            {
                // 与权限判定同源：聚合该角色的**所有** t_ERP_RoleMenu 行。
                // 原先只读 FirstOrDefault 一行 → 多行角色时勾选框会漏显被授权的菜单，
                // 让人误以为没授权（或反过来以为要重新勾）→ 保存后反而把权限改坏。
                foreach (var mid in MenuService.RoleMenuIds(_db, roleId)) checkedIds.Add(mid);
            }

            var all = _db.t_ERP_Menu.OrderBy(m => m.ID).ToList();
            var nodes = all.ToDictionary(
                m => m.ID,
                m => new RoleMenuNode
                {
                    Id = m.ID,
                    Name = m.MenuName,
                    ParentId = m.ParentID,
                    Checked = checkedIds.Contains(m.ID)
                });

            var roots = new List<RoleMenuNode>();
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
            return roots;
        }

        #endregion
    }
}
