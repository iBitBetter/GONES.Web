using GONES.Model.Pg;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Linq;
using System.Security.Claims;

namespace GONES.Web.Filters
{
    /// <summary>
    /// 全局授权过滤器，管两件事：
    /// 1. 用户仍带着“必须改密”标记（MustChangePwd=1 claim）时，把所有请求拦截并重定向到
    ///    /Account/ChangePassword，直到用户改掉初始口令为止。
    ///    claim 由 AccountController 在登录时写入、改密成功后立即清除；因此一步之遥的会话
    ///    盗用（或他人离开终端）都无法在改密前跳到其他页面。
    /// 2. 票据里的口令戳（PwdStamp）与库中当前口令不一致时，把会话踢下线 —— 口令被本人在
    ///    别处改掉、被管理员重置、或被旧 WinForms 客户端改掉之后，别的设备上仍拿着旧票据的
    ///    会话立即失效（否则改密形同虚设）。
    /// </summary>
    public class MustChangePasswordAuthorizationFilter : IAuthorizationFilter
    {
        // 与 AccountController 中的常量保持一致
        private const string MustChangePwdClaim = "MustChangePwd";
        private const string PwdStampClaim = "PwdStamp";
        private const string UserIdClaim = "UserId";

        private readonly GonesPgDbContext _db;

        public MustChangePasswordAuthorizationFilter(GonesPgDbContext db)
        {
            _db = db;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user == null || !user.Identity.IsAuthenticated) return;

            var rd = context.RouteData;
            string controller = (rd.Values["controller"] as string)?.ToLowerInvariant();
            string action = (rd.Values["action"] as string)?.ToLowerInvariant();
            bool onAccount = controller == "account";

            // 登出入口必须先放行：否则重定向到它时又会被本过滤器拦下，形成死循环。
            if (onAccount && (action == "sessionexpired" || action == "logout" || action == "login"))
                return;

            // ---- (1) 待改密：锁在改密页 ----
            bool mustChange = user.HasClaim(c => c.Type == MustChangePwdClaim && c.Value == "1");
            if (mustChange && !(onAccount && action == "changepassword"))
            {
                context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
                return;
            }

            // ---- (2) 口令戳：口令已变更则作废当前票据 ----
            if (!StampMatches(user))
                context.Result = new RedirectToActionResult("SessionExpired", "Account", null);
        }

        /// <summary>
        /// 票据中的口令戳是否仍然有效。
        /// 拿不到戳（升级前签发的票据）或拿不到 UserId 时一律放行：不追溯旧会话，
        /// 免得一次发版把所有人同时踢下线。数据库连不上时同样放行 —— 宁可暂时不校验，
        /// 也不能让全站 500。
        /// </summary>
        private bool StampMatches(ClaimsPrincipal user)
        {
            var stamp = user.FindFirst(PwdStampClaim)?.Value;
            if (string.IsNullOrEmpty(stamp)) return true;

            int userId;
            if (!int.TryParse(user.FindFirst(UserIdClaim)?.Value, out userId)) return true;

            try
            {
                // Security stamp = password hash + status + role (AccountController.SecurityStamp).
                // Comparing all three means a DISABLED or DEMOTED account loses its session
                // on the very next request, not after the 8h sliding expiry.
                var current = _db.t_ERP_UserInfo
                    .Where(u => u.ID == userId)
                    .Select(u => new { u.PassWord, u.Status, u.RoleID })
                    .FirstOrDefault();

                if (current == null) return false;   // 账号已删除：会话必须死
                var expected = (current.PassWord ?? "") + "|" + current.Status + "|" + current.RoleID;
                if (string.Equals(expected, stamp, StringComparison.OrdinalIgnoreCase)) return true;
                // 升级前签发的旧票据戳是纯口令哈希（无 "|"）：按旧口径放行，不追溯踢人，
                // 与"拿不到戳一律放行"的既有取舍一致。新登录/改密后签发的票据走完整安全戳。
                if (stamp.IndexOf('|') < 0)
                    return string.Equals(current.PassWord ?? "", stamp, StringComparison.OrdinalIgnoreCase);
                return false;
            }
            catch (Npgsql.NpgsqlException)
            {
                return true;
            }
        }
    }
}
