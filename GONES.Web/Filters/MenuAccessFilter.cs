using GONES.Model.Pg;
using GONES.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace GONES.Web.Filters
{
    /// <summary>
    /// 标记接口：控制器声明自己受菜单权限守卫。
    /// 实现它＝整个控制器的所有 Action 自动获得入口闸门，无需在每个 Action 里手写
    /// <c>if (!CanAccess) return ...</c>；新增 Action 也不会因为漏写而裸奔。
    /// 未实现的控制器（Home / Account / Dashboard 等）完全不受影响。
    /// </summary>
    public interface IMenuGuarded
    {
    }

    /// <summary>
    /// 集中式菜单权限过滤器（取代原先散落在 27 个控制器、260 余处的手写 CanAccess 判断）。
    ///
    /// 判定：一律走 <see cref="MenuService.CanAccessRoute"/>，唯一真相源仍是 t_erp_menu.route 列 ——
    /// 本过滤器只决定"在哪拦"和"拦了怎么回"，不引入第二套权限口径。
    ///
    /// 拒绝形态（对齐改造前的手写语义）：
    ///  - 数据接口（Action 名以 Get/Search/Load/Query/Trace/Export/List 开头，或带 X-Requested-With）
    ///    → 401 Unauthorized，避免前端 fetch 拿到一段首页 HTML 后 res.json() 抛错；
    ///  - 页面请求 → 重定向首页（与原先 RedirectToAction("Index","Home") 一致）。
    ///
    /// Fail-closed：实现了 <see cref="IMenuGuarded"/> 但菜单表缺 route 的控制器，判定为无权限（拒绝而非放行）。
    ///
    /// ── 只读角色约束（2026-09-30 新增）──────────────────────────────────────────
    /// 角色 t_ERP_Role.is_readonly=1（如「生产系统查询」）仅能查询已授权菜单，不能操作。
    /// 判定口径（fail-closed）：
    ///  - 纯写动作（<see cref="WriteActionBlacklist"/>：Delete/Audit/Recalc/Disable/Enable/…）
    ///    无 GET 查看页面，GET 直接触发数据变更，故任何 HTTP 方法都拦截（防 GET 删除/审核绕过）；
    ///  - 非 GET 且非查询前缀接口（如 POST 的 Create/Edit 提交、POST 的任意未列查询前缀动作）
    ///    一律拦截；
    ///  - GET 页面加载（Index/Create/Edit/Details/Form 等）与查询接口（Get/Search/List/Export/Trace…）
    ///    放行（只读角色应能打开页面查看、执行查询与导出）。
    /// Create/Edit 不进黑名单：它们既有 GET 页面版（查看）又有 POST 提交版（写），由 HTTP 方法区分。
    /// </summary>
    public class MenuAccessFilter : IAsyncActionFilter
    {
        // ⚠ 前缀判定是「X-Requested-With 缺失时的兜底」，不是主判据：前端规范应始终带该头。
        //   List 前缀是 2026-09-29 补的 —— StockBillController.ListItems 返回 JSON 却不在名单，
        //   前端若忘了带 X-Requested-With，被拒时会拿到 302 重定向的整页 HTML（res.json() 直接炸），
        //   而不是干净的 401。新增任何「返回 JSON 但名字不以 Get/Search/... 开头」的 Action 都要补进来。
        private static readonly string[] DataActionPrefixes = { "Get", "Search", "Load", "Query", "Trace", "Export", "List" };

        // 纯写动作（无 GET 查看页面，GET 直接触发数据变更）：即便 GET 也拦截。
        // Create/Edit 不在此列——它们既有 GET 页面版（查看）又有 POST 提交版（写），
        // 由「HTTP 方法」区分（GET 放行查看、POST 拦截提交）。
        private static readonly HashSet<string> WriteActionBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Delete", "Audit", "UnAudit", "Recalc", "Disable", "Enable",
            "ToggleStop", "Copy", "ImportList", "ImportFromBillUse"
        };

        private readonly GonesPgDbContext _db;
        private readonly MenuService _menu;

        public MenuAccessFilter(GonesPgDbContext db, MenuService menu)
        {
            _db = db;
            _menu = menu;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.Controller is not IMenuGuarded)
            {
                await next();
                return;
            }

            var controllerName = context.RouteData.Values["controller"]?.ToString();
            var principal = context.HttpContext.User;

            if (_menu.CanAccessRoute(_db, principal, controllerName))
            {
                // 只读角色：仅放行 GET 与查询类接口，拦截一切写操作（fail-closed）。
                if (IsWriteBlockedForReadOnly(principal, GetActionName(context), context.HttpContext.Request.Method))
                {
                    Reject(context);
                    return;
                }
                await next();
                return;
            }

            Reject(context);
        }

        private static void Reject(ActionExecutingContext context)
        {
            var request = context.HttpContext.Request;
            var isXhr = string.Equals(request.Headers["X-Requested-With"].ToString(),
                "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
            var actionName = GetActionName(context);
            var isDataAction = DataActionPrefixes.Any(p => actionName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            context.Result = (isXhr || isDataAction)
                ? new UnauthorizedResult()
                : new RedirectToActionResult("Index", "Home", null);
        }

        private static string GetActionName(ActionExecutingContext context)
            => (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? string.Empty;

        private static int GetRoleId(ClaimsPrincipal principal)
        {
            int.TryParse(principal.FindFirst("RoleId")?.Value, out int rid);
            return rid;
        }

        /// <summary>
        /// 只读角色是否应拦截本次请求：纯写动作（任何方法）或非 GET 且非查询接口，均拦截；
        /// GET 页面加载与查询接口放行。
        /// </summary>
        private bool IsWriteBlockedForReadOnly(ClaimsPrincipal principal, string actionName, string method)
        {
            if (!MenuService.IsReadOnlyRole(_db, GetRoleId(principal))) return false;
            if (WriteActionBlacklist.Contains(actionName)) return true;
            bool isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
            bool isQueryAction = DataActionPrefixes.Any(p => actionName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            return !isGet && !isQueryAction;
        }
    }
}
