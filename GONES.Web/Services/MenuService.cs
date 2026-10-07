using System.Collections.Generic;
using System.Linq;
using Furion.DependencyInjection;
using GONES.Model.Pg;
using GONES.Web.Models;

namespace GONES.Web.Services
{
    /// <summary>
    /// 菜单解析：复刻旧 FrmLogin 的权限判定（用户表已切换为 t_ERP_UserInfo）。
    ///  - 管理员(RoleID&lt;=3，即旧系统"看全部菜单"的门槛) => 全部功能菜单 (menuService.GetAll)
    ///  - 普通用户(RoleID&gt;3) => 角色菜单(t_ERP_RoleMenu)
    /// 注：t_ERP_UserInfo 无 FAdmin/FMenuIdEdit/FMenuIdView 字段，故不再有个人菜单叠加。
    /// Furion: 实现 ISingleton 标记接口 → AddInject 自动注册，无需显式 AddSingleton。
    ///
    /// ⚠⚠ 菜单权限口径（2026-09-24 整改，改前必读）：
    ///   **唯一真相源 = t_ERP_Menu.route 列**：菜单行自带 Web 路由（如 <c>/PmsItem</c>），
    ///   侧边栏 URL 与控制器入口闸门 <see cref="CanAccessRoute"/> 全部从这里派生，
    ///   代码里不再有任何 MenuId 常量、也不再有“类名 → 路由”映射字典。
    ///   历史坑：① 控制器曾硬编码 <c>private const int MenuId</c>，且按旧 WinForms 菜单号写成
    ///   1200+ 段（1293 仓库领料、1289 人员管理），而 t_ERP_Menu 实际只有 1..35 行 → 非管理员
    ///   角色的授权 ID（20/24/35）与常量永远对不上 → 所有入口静默 302 踢回首页，且 admin 因
    ///   旁路放行完全无感（表现：菜单看得见但点进去全打不开）。
    ///   ② 旧窗体类名 → 路由的映射曾硬编码为 WebRoutes 字典，与菜单表构成两套真相。
    ///   现两者均已收敛进 t_ERP_Menu.route（灌数脚本：<c>temp/menu_route_seed.sql</c>）。
    ///   **新增控制器/菜单时：只需保证菜单行 route 列 = "/" + ControllerName，无需改任何常量。**
    /// </summary>
    public class MenuService : ISingleton
    {
        public List<MenuNode> GetMenuTree(GonesPgDbContext db, bool isAdmin, t_ERP_UserInfo user)
        {
            // 全量表用于祖先可见性判定：禁用菜单(Flag=0)及其整棵子树都不显示。
            var allMenus = db.t_ERP_Menu.ToList();
            List<t_ERP_Menu> allowed = isAdmin
                ? allMenus
                : ResolveUserMenus(db, user);
            return BuildTree(allowed, allMenus);
        }

        /// <summary>
        /// 当前用户可访问的 Web 路由集合（与侧边栏菜单同源、同口径）。
        ///  - 管理员(RoleID 1..3) = 全部已迁移路由；
        ///  - 普通用户 = 角色授权菜单里已迁移的路由。
        /// "#"（未迁移的旧窗体）不计入；Flag=0 及其子树同样被 <see cref="GetMenuTree"/> 的可见性规则剔除。
        /// 用途：首页快捷入口按账号实际权限过滤（没有权限的入口直接不显示）。
        /// </summary>
        public HashSet<string> GetAccessibleUrls(GonesPgDbContext db, bool isAdmin, t_ERP_UserInfo user)
        {
            var urls = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var tree = GetMenuTree(db, isAdmin, user);
            var stack = new Stack<MenuNode>(tree);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!string.IsNullOrEmpty(n.Url) && n.Url != "#") urls.Add(n.Url);
                if (n.Children != null)
                    foreach (var c in n.Children) stack.Push(c);
            }
            return urls;
        }

        /// <summary>
        /// 管理员判定：对齐旧 FrmLogin 语义 —— RoleID==3 取全部菜单，RoleID&gt;3 才按角色裁剪；
        /// RoleID 1/2 属超级管理员，同样给全部菜单。
        /// </summary>
        public bool IsAdmin(t_ERP_UserInfo user)
        {
            if (user == null) return false;
            // RoleID 1/2/3 are the admin roles. A lower bound is mandatory: RoleID is a plain
            // int whose default is 0, and "no role assigned" (or a role row deleted from
            // t_ERP_Role) used to satisfy `<= 3` and hand out full admin rights.
            return user.RoleID >= 1 && user.RoleID <= 3;
        }

        private List<t_ERP_Menu> ResolveUserMenus(GonesPgDbContext db, t_ERP_UserInfo user)
        {
            // The user row can be missing (deleted while the auth cookie is still valid);
            // that must degrade to "no menus", not a NullReferenceException in the layout.
            if (user == null) return new List<t_ERP_Menu>();

            var ids = RoleMenuIds(db, user.RoleID);
            if (ids.Count == 0) return new List<t_ERP_Menu>();

            var allowed = db.t_ERP_Menu.Where(m => ids.Contains(m.ID)).ToList();
            return IncludeAncestors(allowed, db.t_ERP_Menu.ToList());
        }

        /// <summary>
        /// 授权任一子菜单时，自动补齐其祖先链（父级目录菜单仅作容器显示，不代表有访问权）。
        /// 若不补齐：BuildTree 只在授权集合内建节点，父目录缺失会把子菜单顶成一级根节点
        /// （实例：getgoods/生产要货员 角色授权了报表叶子但漏了父目录 1281「生产 - 报表管理」）。
        /// </summary>
        private static List<t_ERP_Menu> IncludeAncestors(List<t_ERP_Menu> allowed, List<t_ERP_Menu> allMenus)
        {
            var byId = allMenus.ToDictionary(m => m.ID, m => m);
            var inSet = new HashSet<int>(allowed.Select(m => m.ID));
            var result = new List<t_ERP_Menu>(allowed);

            foreach (var m in allowed)
            {
                var cur = m;
                var guard = new HashSet<int>();   // 防环：坏数据 ParentID 成环时终止上溯
                while (cur.ParentID.HasValue && cur.ParentID.Value != 0
                       && guard.Add(cur.ParentID.Value)
                       && byId.TryGetValue(cur.ParentID.Value, out var parent))
                {
                    if (inSet.Add(parent.ID)) result.Add(parent);
                    cur = parent;
                }
            }
            return result;
        }

        /// <summary>
        /// 某个角色被授权的菜单 ID 集合。
        /// 注意 <c>t_ERP_RoleMenu</c> 里**一个角色可能有多行**（现存 role 20 就是两行）——
        /// 早期实现用 <c>FirstOrDefault</c> 只取一行，会静默丢掉后续行的授权，故此处聚合全部行。
        ///
        /// public：这是「角色 → 菜单集合」的唯一真相源，权限判定（<see cref="CanAccessRoute"/>）、
        /// 侧边栏（<see cref="ResolveUserMenus"/>）与角色权限页（<c>SysRoleController</c>）都必须
        /// 走它。SysRoleController 曾各自 <c>FirstOrDefault</c> 只读一行 → 多行角色下 UI 显示/
        /// 收回权限都只作用于第一行，而此处聚合所有行使其余行依旧生效（静默权限泄漏）。
        /// </summary>
        public static HashSet<int> RoleMenuIds(GonesPgDbContext db, int roleId)
        {
            var ids = new HashSet<int>();
            if (roleId <= 0) return ids;      // "无角色"（默认 0）不得继承任何菜单
            foreach (var rm in db.t_ERP_RoleMenu.Where(r => r.RoleID == roleId).ToList())
                AddIds(ids, rm.MenuID);       // 形如 "1280;1293;1294"
            return ids;
        }

        /// <summary>
        /// 只读角色判定：t_ERP_Role.is_readonly == 1 的角色（如「生产系统查询」）仅能查询，
        /// 不能对任何已授权菜单执行增删改审等操作。与 <see cref="RoleMenuIds"/> 同源风格，
        /// 是「角色 → 只读约束」的唯一真相源，供 <see cref="MenuAccessFilter"/> 在入口处拦截写操作。
        /// </summary>
        public static bool IsReadOnlyRole(GonesPgDbContext db, int roleId)
        {
            if (roleId <= 0) return false;
            var role = db.t_ERP_Role.FirstOrDefault(r => r.ID == roleId);
            return role != null && role.IsReadonly == 1;
        }

        /// <summary>
        /// 入口闸门判定（单一真相源版）：按控制器名反查 t_ERP_Menu.route 得到菜单 ID 集合，
        /// 再与角色授权集合求交集。控制器不再写任何 MenuId 常量，口径唯一来源 = 菜单表 route 列。
        /// admin（RoleID 1..3）走旁路恒 true。
        /// </summary>
        public bool CanAccessRoute(GonesPgDbContext db, System.Security.Claims.ClaimsPrincipal principal, string controllerName)
        {
            if (principal == null || string.IsNullOrEmpty(controllerName)) return false;
            if (principal.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1")) return true;
            if (!int.TryParse(principal.FindFirst("RoleId")?.Value, out int roleId)) return false;

            var allowed = RoleMenuIds(db, roleId);
            if (allowed.Count == 0) return false;

            var route = "/" + controllerName;
            // 菜单表仅数十行：客户端比对规避 ILike/Lower 的翻译差异；
            // 一个路由允许对应多行（历史存在同路由多菜单），任一被授权即放行。
            var menuIds = db.t_ERP_Menu
                .Where(m => m.Route != null)
                .Select(m => new { m.ID, m.Route })
                .ToList()
                .Where(m => string.Equals(m.Route, route, System.StringComparison.OrdinalIgnoreCase))
                .Select(m => m.ID)
                .ToList();

            return menuIds.Count > 0 && menuIds.Any(allowed.Contains);
        }

        private static void AddIds(HashSet<int> ids, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            foreach (var s in csv.Split(';'))
                if (int.TryParse(s.Trim(), out int id)) ids.Add(id);
        }

        // ── 路由 ↔ 业务模块 对照（纯文档，不参与运行时判定）───────────────────────
        // 运行时唯一依据 = t_ERP_Menu.route 列（灌数脚本 temp/menu_route_seed.sql）。
        // 基础资料：/PmsItemClass 商品分类  /PmsItem 商品  /PmsStep 工序  /PmsBom BOM  /DataDict 码表
        // 系统管理：/SysDepartment 部门  /SysRole 角色  /SysMenu 菜单  /SysUser 用户  /PmsWorker 人员  /SysShop 店铺
        // 生产管理：/PmsProduceOrder 生产单  /GoodsApply 要货计划
        // 仓库管理：/StockBill 采购入库(FBillType=0,CGRKD)  /ProductIn 生产入库(1,SCRKD)
        //           /WarehousePick 仓库领料(8,SCLLD)  /WarehouseReturn 仓库退料(3,TIN)
        //           /DepartmentPick 部门领料(9,BMLLD)  /DepartmentReturn 部门退料
        //           /ExchangeIn 换货入库(6,HHRK)  /ProductLoss 产品报损(5,CPBSD)
        //           /StockCheck 仓库盘点  /BatchStock 批次余额
        // 报表管理：/StockStat 进销存统计  /ProductBomStat 产品结构图汇总
        //           /ProductInOutStock 收发存统计  /ProductInOutCount 入出库统计

        // allowed = 当前用户被允许看到的菜单（管理员=全量，普通用户=角色菜单）；
        // allMenus = 全量表，用于解析祖先链。约定：Flag=0 的菜单不启用，自身及其
        // 整棵子树前台均不显示（即便子项 Flag=1，只要某一级祖先 Flag=0 就隐藏）。
        private static List<MenuNode> BuildTree(List<t_ERP_Menu> allowed, List<t_ERP_Menu> allMenus)
        {
            var allById = allMenus.ToDictionary(m => m.ID, m => m);
            var visibility = new Dictionary<int, bool>();

            bool IsVisible(t_ERP_Menu m, HashSet<int> visiting)
            {
                if (visibility.TryGetValue(m.ID, out var v)) return v;
                if (visiting.Contains(m.ID)) return false;   // 防环
                visiting.Add(m.ID);
                bool result = m.Flag;
                if (result && m.ParentID.HasValue && m.ParentID.Value != 0
                    && allById.TryGetValue(m.ParentID.Value, out var parent))
                {
                    result = IsVisible(parent, visiting);
                }
                visiting.Remove(m.ID);
                visibility[m.ID] = result;
                return result;
            }

            var nodes = new Dictionary<int, MenuNode>();
            foreach (var m in allowed)
            {
                if (!IsVisible(m, new HashSet<int>())) continue;
                nodes[m.ID] = new MenuNode
                {
                    Id = m.ID,
                    Name = m.MenuName,
                    ParentId = m.ParentID,
                Url = string.IsNullOrEmpty(m.Route) ? "#" : m.Route
                };
            }

            var roots = new List<MenuNode>();
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
            SortTree(roots, allById);
            return roots;
        }

        /// <summary>
        /// 整棵树按 t_ERP_Menu.Operating 升序递归排序：Operating 为 NULL 的落末尾，
        /// 同级同 Operating 再按 ID 兜底，保证左侧菜单（含每一层子菜单）顺序稳定、
        /// 与旧系统一致，且不依赖 EF 返回的（不确定的）物理顺序。
        /// </summary>
        private static void SortTree(List<MenuNode> nodes, Dictionary<int, t_ERP_Menu> byId)
        {
            nodes.Sort((a, b) =>
            {
                int oa = (byId.TryGetValue(a.Id, out var ma) && ma.Operating.HasValue) ? ma.Operating.Value : int.MaxValue;
                int ob = (byId.TryGetValue(b.Id, out var mb) && mb.Operating.HasValue) ? mb.Operating.Value : int.MaxValue;
                if (oa != ob) return oa.CompareTo(ob);
                return a.Id.CompareTo(b.Id);
            });
            foreach (var n in nodes)
                if (n.Children != null) SortTree(n.Children, byId);
        }
    }
}
