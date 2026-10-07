using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
    /// 用户管理 —— 旧 WinForms SysManager.FrmUserList / FrmUserInfoEdit 的 Web 版（数据表 t_ERP_UserInfo）。
    /// 旧窗体基于 t_Base_User，Web 已统一迁移到 t_ERP_UserInfo：账号=Account、姓名=UserName、部门=DEPID、角色=RoleID。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400，走标准 MVC 表单回显
    public class SysUserController : Controller, IMenuGuarded
    {
        private readonly GonesPgDbContext _db;
        public SysUserController(GonesPgDbContext db, MenuService menu) { _db = db; _menu = menu; }



        private readonly MenuService _menu;



        /// <summary>
        /// 菜单授权只回答"能不能进用户管理页"；角色指派/管理员账户保护必须二次校验。
        /// 否则任何被授予菜单 16 的普通角色都可以：把任意账号（含自己）的 RoleID 改成
        /// 1..3 完成提权，或对 admin 勾选"重置密码"拿到明文初始口令接管系统。
        /// 非管理员仍可管理普通账号（委托建号场景），但碰不到管理员角色与管理员账户。
        /// </summary>
        private bool AmAdmin => User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

        // GET /SysUser
        public IActionResult Index(int? depid, int page = 1, int pageSize = 20)
        {
            if (page < 1) page = 1;
            pageSize = PagerViewModel.NormalizePageSize(pageSize);

            var allDeps = _db.t_ERP_Department.OrderBy(d => d.ID).ToList();
            ViewBag.DepTree = DepartmentTree.Build(allDeps);
            ViewBag.CurrentDepId = depid ?? 0;

            IQueryable<t_ERP_UserInfo> query = _db.t_ERP_UserInfo.AsQueryable();
            if (depid.HasValue && depid.Value > 0)
            {
                // 按部门过滤：包含该部门自身及其全部子部门下的用户
                var ids = DepartmentTree.CollectSubtreeIds(allDeps, depid.Value);
                query = query.Where(u => ids.Contains(u.DEPID));
            }
            query = query.OrderBy(u => u.ID);
            int total = query.Count();
            page = PagerViewModel.ClampPage(page, total, pageSize);
            var users = query.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            ViewBag.DepartmentDict = _db.t_ERP_Department.ToList()
                .ToDictionary(d => d.ID, d => d.DEPName ?? ("#" + d.ID));
            ViewBag.RoleDict = _db.t_ERP_Role.ToList()
                .ToDictionary(r => r.ID, r => r.RoleName ?? ("#" + r.ID));

            ViewBag.Pager = new PagerViewModel
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                Action = "Index",
                ExtraValues = (depid.HasValue && depid.Value > 0)
                    ? new Dictionary<string, object> { ["depid"] = depid.Value }
                    : null,
            };
            return View(users);
        }

        // GET /SysUser/Create
        [HttpGet]
        public IActionResult Create()
        {
            PrepareSelectLists();
            return View("Form", new UserEditViewModel { Enabled = true, CanLogin = true });
        }

        // POST /SysUser/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(UserEditViewModel vm)
        {
            PrepareSelectLists();
            if (!ModelState.IsValid) return View("Form", vm);

            if (_db.t_ERP_UserInfo.Any(u => u.Account == vm.Account.Trim()))
            {
                ModelState.AddModelError(string.Empty, "账号已存在！");
                return View("Form", vm);
            }
            // 非管理员不能创建管理员角色的账号（防提权，见 AmAdmin 注释）。
            if (!AmAdmin && IsAdminRole(vm.RoleID))
            {
                ModelState.AddModelError(nameof(vm.RoleID), "只有管理员才能分配管理员角色。");
                return View("Form", vm);
            }

            if (!ValidateDepartmentAndRole(vm)) return View("Form", vm);
            if (!ValidateDepots(vm)) return View("Form", vm);

            // 新建用户不再共用明文 "123"：分配一个随机初始口令，并置 PwdMustChange=1，
            // 强制使用者在首次登录后立即修改，避免 Web 端口令被局域网内他人冒用。
            var initialPwd = PasswordHelper.GenerateInitialPassword();
            var entity = new t_ERP_UserInfo
            {
                Account = vm.Account.Trim(),
                PassWord = PasswordHelper.Md5(initialPwd),
                UserName = vm.UserName.Trim(),
                DEPID = vm.DEPID ?? 0,
                RoleID = vm.RoleID ?? 0,
                Status = vm.Enabled ? (byte)1 : (byte)0,
                IsLogin = vm.CanLogin ? (byte)1 : (byte)0,
                Shop = (vm.ShopIds != null && vm.ShopIds.Any())
                    ? string.Join(",", vm.ShopIds) : null,
                Depot = JoinDepots(vm),
                PwdMustChange = true,
            };

            // ⚠ `t_ERP_UserInfo.ID` 是**非自增**主键（手工分配）。原先此处不给 ID 就直接 Add，
            //   于是 ID 取 int 默认值 **0**：第一个用户占掉 0，**从第二个用户起必然撞主键**
            //   （Violation of PRIMARY KEY, duplicate key (0)）。与其它手工主键表保持一致：
            //   在共享锁内「读 max → 赋 ID → 落库」，避免并发同时取到同一个号。
            lock (StockService.BillNoLock)
            {
                entity.ID = (_db.t_ERP_UserInfo.Max(u => (int?)u.ID) ?? 0) + 1;
                _db.t_ERP_UserInfo.Add(entity);
                _db.SaveChanges();
            }
            TempData["Success"] = "用户新建成功，初始密码为：" + initialPwd + "（首次登录后必须修改）。";
            return RedirectToAction(nameof(Index));
        }

        // GET /SysUser/Edit/5
        [HttpGet]
        public IActionResult Edit(int id)
        {
            var u = _db.t_ERP_UserInfo.FirstOrDefault(x => x.ID == id);
            if (u == null) return NotFound();
            PrepareSelectLists();

            var vm = new UserEditViewModel
            {
                ID = u.ID,
                Account = u.Account,
                UserName = u.UserName,
                DEPID = u.DEPID,
                RoleID = u.RoleID,
                Enabled = u.Status == 1,
                CanLogin = u.IsLogin == 1,
                ShopIds = string.IsNullOrEmpty(u.Shop)
                    ? new List<int>()
                    : u.Shop.Split(',')
                           .Where(s => int.TryParse(s.Trim(), out _))
                           .Select(s => int.Parse(s.Trim()))
                           .ToList(),
                DepotIds = string.IsNullOrEmpty(u.Depot)
                    ? new List<int>()
                    : u.Depot.Split(',')
                            .Where(s => int.TryParse(s.Trim(), out _))
                            .Select(s => int.Parse(s.Trim()))
                            .ToList(),
            };
            return View("Form", vm);
        }

        // POST /SysUser/Edit/5
        /// <summary>Admin role range, mirrors MenuService.IsAdmin: t_ERP_Role starts at 3 and RoleID=0 means no role at all.</summary>
        private static bool IsAdminRole(int? roleId) => roleId.HasValue && roleId.Value >= 1 && roleId.Value <= 3;

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, UserEditViewModel vm)
        {
            PrepareSelectLists();
            if (id != vm.ID) return BadRequest();

            var u = _db.t_ERP_UserInfo.FirstOrDefault(x => x.ID == id);
            if (u == null) return NotFound();

            if (!ModelState.IsValid) return View("Form", vm);

            // Guard against locking yourself out. Delete already refuses to remove the
            // signed-in account; without the same guard here an admin could disable their
            // own account or drop their own admin role, leaving nobody able to reach the
            // back office (recoverable only by editing the table directly).
            int.TryParse(User.FindFirst("UserId")?.Value, out int me);
            if (me > 0 && me == id)
            {
                if (!vm.Enabled)
                {
                    ModelState.AddModelError(string.Empty, "\u4e0d\u80fd\u505c\u7528\u5f53\u524d\u767b\u5f55\u7684\u8d26\u6237\u3002");   // 不能停用当前登录的账户。
                    return View("Form", vm);
                }
                if (!IsAdminRole(vm.RoleID))
                {
                    ModelState.AddModelError(string.Empty, "\u4e0d\u80fd\u53d6\u6d88\u5f53\u524d\u767b\u5f55\u8d26\u6237\u7684\u7ba1\u7406\u5458\u89d2\u8272\u3002");   // 不能取消当前登录账户的管理员角色。
                    return View("Form", vm);
                }
            }

            if (_db.t_ERP_UserInfo.Any(x => x.ID != id && x.Account == vm.Account.Trim()))
            {
                ModelState.AddModelError(string.Empty, "账号已存在！");
                return View("Form", vm);
            }

            // 防提权（见 AmAdmin 注释）：非管理员既不能把任何账号提升为管理员角色，
            // 也不能碰已有的管理员账户（停用/降级/改信息/重置密码都算）。
            if (!AmAdmin)
            {
                if (IsAdminRole(vm.RoleID))
                {
                    ModelState.AddModelError(nameof(vm.RoleID), "只有管理员才能分配管理员角色。");
                    return View("Form", vm);
                }
                if (IsAdminRole(u.RoleID))
                {
                    ModelState.AddModelError(string.Empty, "只有管理员才能修改管理员账户。");
                    return View("Form", vm);
                }
            }

            if (!ValidateDepartmentAndRole(vm)) return View("Form", vm);
            if (!ValidateDepots(vm)) return View("Form", vm);

            u.Account = vm.Account.Trim();
            u.UserName = vm.UserName.Trim();
            u.DEPID = vm.DEPID ?? 0;
            u.RoleID = vm.RoleID ?? 0;
            u.Status = vm.Enabled ? (byte)1 : (byte)0;
            u.IsLogin = vm.CanLogin ? (byte)1 : (byte)0;
            u.Shop = (vm.ShopIds != null && vm.ShopIds.Any())
                ? string.Join(",", vm.ShopIds) : null;
            u.Depot = JoinDepots(vm);

            if (vm.ResetPassword)
            {
                // 重置同样不再回退到明文 "123"：重新分配随机初始口令并强制下次登录修改。
                var initialPwd = PasswordHelper.GenerateInitialPassword();
                u.PassWord = PasswordHelper.Md5(initialPwd);
                u.PwdMustChange = true;
                TempData["Success"] = "用户修改成功。密码已重置为初始密码：" + initialPwd + "（首次登录后必须修改）。";
            }
            else
            {
                TempData["Success"] = "用户修改成功。";
            }

            _db.SaveChanges();
            return RedirectToAction(nameof(Index));
        }

        // POST /SysUser/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var u = _db.t_ERP_UserInfo.FirstOrDefault(x => x.ID == id);
            if (u == null) return NotFound();

            int.TryParse(User.FindFirst("UserId")?.Value, out int me);
            if (me == id)
            {
                TempData["Error"] = "不能删除当前登录的账户。";
                return RedirectToAction(nameof(Index));
            }
            // 防提权：非管理员不能删除管理员账户（见 AmAdmin 注释）。
            if (!AmAdmin && IsAdminRole(u.RoleID))
            {
                TempData["Error"] = "只有管理员才能删除管理员账户。";
                return RedirectToAction(nameof(Index));
            }

            _db.t_ERP_UserInfo.Remove(u);
            _db.SaveChanges();
            TempData["Success"] = "用户删除成功。";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// DEPID / RoleID 由前端下拉提交，POST 可被篡改。写入前必须确认目标真实存在，
        /// 否则留下脏 FK：用户归属部门解析为空白、菜单/权限按 RoleID 判定会静默失效。
        /// 部门判定口径必须与下拉一致：**只有 IsDEP == 0 才算部门**（1 仓库 / 2 车间 / 3 班组都不算）。
        /// </summary>
        private bool ValidateDepartmentAndRole(UserEditViewModel vm)
        {
            bool ok = true;
            if (vm.DEPID.HasValue && vm.DEPID.Value > 0
                && !_db.t_ERP_Department.Any(d => d.ID == vm.DEPID.Value && d.IsDEP == 0))
            {
                ModelState.AddModelError(nameof(vm.DEPID), "所选部门不存在或不是部门，请重新选择。");
                ok = false;
            }
            if (vm.RoleID.HasValue && vm.RoleID.Value > 0
                && !_db.t_ERP_Role.Any(r => r.ID == vm.RoleID.Value))
            {
                ModelState.AddModelError(nameof(vm.RoleID), "所选角色不存在，请重新选择。");
                ok = false;
            }
            return ok;
        }

        /// <summary>
        /// 仓库权限（Depot）落库前校验。Depot 是逗号串，多选提交可被篡改：
        /// 写入不存在的 ID（历史脏值如 63）会让该账号的仓库范围多出「幽灵项」，
        /// 而 DepotScope 每次读取都要丢弃它 —— 这里直接拒绝，保持数据干净。
        /// 判定口径必须与 DepotScope 一致：**只有 t_ERP_Department.IsDEP == 1 才算仓库**。
        /// </summary>
        private bool ValidateDepots(UserEditViewModel vm)
        {
            var ids = (vm.DepotIds ?? new List<int>()).Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return true;   // 留空＝不配任何仓库（管理员 RoleID 1..3 不受限）
            var bad = ids.Where(i => !_db.t_ERP_Department.Any(d => d.ID == i && d.IsDEP == 1)).ToList();
            if (bad.Count > 0)
            {
                ModelState.AddModelError(nameof(vm.DepotIds),
                    "所选仓库不存在或不是仓库（ID：" + string.Join(",", bad) + "），请重新选择。");
                return false;
            }
            return true;
        }

        /// <summary>
        /// DepotIds → 落库用逗号串。
        /// ⚠ `t_ERP_UserInfo.Depot` 是 **NOT NULL**（与**可空**的 `Shop` 不同！），
        /// 所以空集合必须落**空串**而不是 null —— 落 null 会在 SaveChanges 时抛
        /// DbEntityValidationException，Create（不带仓库）与 Edit（清空仓库）两条路径都会 500。
        /// 空串的语义与「未配置仓库」一致：DepotScope 视其为无仓库 → 非管理员 fail-closed。
        /// </summary>
        private static string JoinDepots(UserEditViewModel vm)
            => (vm.DepotIds != null && vm.DepotIds.Any())
                ? string.Join(",", vm.DepotIds.Where(i => i > 0).Distinct())
                : "";

        private void PrepareSelectLists()
        {
            // 部门候选：只取 IsDEP=0（真正的行政/业务部门）。
            // ⚠ IsDEP 有 4 个值（0 部门 / 1 仓库 / 2 车间 / 3 班组）——必须写死 ==0，
            //   否则会把「成品仓库 / 包装车间 / 包装一组」这类非部门混进「部门」下拉。
            ViewBag.Departments = _db.t_ERP_Department.Where(d => d.IsDEP == 0).OrderBy(d => d.ID)
                .Select(d => new SelectListItem { Value = d.ID.ToString(), Text = d.DEPName })
                .ToList();
            ViewBag.Roles = _db.t_ERP_Role.OrderBy(r => r.ID)
                .Select(r => new SelectListItem { Value = r.ID.ToString(), Text = r.RoleName })
                .ToList();
            ViewBag.Shops = _db.t_ERP_ShopInfo.OrderBy(s => s.ID)
                .Select(s => new SelectListItem { Value = s.ID.ToString(), Text = s.ShopName })
                .ToList();
            // 仓库权限候选项：只取 IsDEP=1。
            // ⚠ IsDEP 有 4 个值（0 部门 / 1 仓库 / 2 车间 / 3 班组）——写死 ==1，
            //   否则会把「72 原材料库 / 73 包装物库」这类**车间**当成仓库混进下拉。
            ViewBag.Depots = _db.t_ERP_Department.Where(d => d.IsDEP == 1).OrderBy(d => d.ID)
                .Select(d => new SelectListItem { Value = d.ID.ToString(), Text = d.DEPName })
                .ToList();

            // 「姓名」改为从人员管理挑选：候选来自 t_PMS_Worker，并预算好每个人的
            // 可落库部门（冒泡到 IsDEP==0，见 PersonPickItem 注释）。
            ViewBag.Persons = LoadPersonOptions();

            // 已占用账号（账号 → 用户 ID）：选择人员时前端即时提示账号冲突，
            // 避免"填完一整页才被告知账号已存在"。字典大小写不敏感，与登录查询口径一致。
            ViewBag.UsedAccounts = _db.t_ERP_UserInfo.AsEnumerable()
                .Where(u => !string.IsNullOrEmpty(u.Account))
                .GroupBy(u => u.Account.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ID, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 人员管理（t_PMS_Worker）→ 姓名下拉候选。
        /// 同名人员只保留 ID 最小的一条：下拉 value 是姓名文本（与 UserName 字段同构），
        /// 重复姓名会生成同值 option 造成"选了没反应"的错觉；真正的同名区分靠下拉文案里的部门名。
        /// DEPID &lt;= 0 或姓名空白的历史脏数据直接跳过。
        /// </summary>
        private List<PersonPickItem> LoadPersonOptions()
        {
            var depById = _db.t_ERP_Department.ToList().ToDictionary(d => d.ID);
            var result = new List<PersonPickItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var w in _db.t_PMS_Worker.OrderBy(w => w.ID).ToList())
            {
                string name = (w.FName ?? string.Empty).Trim();
                if (name.Length == 0 || !seen.Add(name)) continue;

                int depId = ResolveDepId(depById, w.DEPID);
                result.Add(new PersonPickItem
                {
                    WorkerId = w.ID,
                    Name = name,
                    DepId = depId,
                    DepName = (depId > 0 && depById.TryGetValue(depId, out var dep)) ? dep.DEPName : null,
                });
            }
            return result;
        }

        /// <summary>
        /// 从人员挂载的组织节点向上找**最近的真部门**（IsDEP == 0）。
        /// 例：包装一组(3) → 包装车间(2) → 生产部(0) ⇒ 返回生产部 ID。
        /// visited 防部门表 ParentID 成环时死循环；链上找不到真部门则返回 0（前端留空）。
        /// </summary>
        private static int ResolveDepId(Dictionary<int, t_ERP_Department> depById, int startId)
        {
            var visited = new HashSet<int>();
            int cur = startId;
            while (cur > 0 && visited.Add(cur) && depById.TryGetValue(cur, out var dep))
            {
                if (dep.IsDEP == 0) return dep.ID;
                cur = dep.ParentID ?? 0;
            }
            return 0;
        }
    }
}
