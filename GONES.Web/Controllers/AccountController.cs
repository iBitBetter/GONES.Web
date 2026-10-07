using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;

namespace GONES.Web.Controllers
{
    [Microsoft.AspNetCore.Mvc.NonValidation]   // 跳过 Furion 的 ModelState 短路 400（裸错误页），走标准 MVC 表单回显；与全项目其他控制器一致
    public class AccountController : Controller
    {
        private readonly GonesPgDbContext _db;
        private readonly MenuService _menuService;
        private readonly ILogger<AccountController> _logger;

        // Every failed login answers with this same sentence. Distinguishing "no such
        // account" / "disabled" / "wrong password" in the UI would turn the login form
        // into a user-name oracle; the real reason goes to the log instead.
        private const string InvalidCredentialMessage = "\u7528\u6237\u540d\u6216\u5bc6\u7801\u9519\u8bef";   // 用户名或密码错误

        public AccountController(GonesPgDbContext db, MenuService menuService,
                                 ILogger<AccountController> logger)
        {
            _db = db;
            _menuService = menuService;
            _logger = logger;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login(string returnUrl = null)
        {
            if (User.Identity.IsAuthenticated)
                return RedirectToAction("Index", "Home");
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model, string returnUrl = null)
        {
            if (!ModelState.IsValid) return View(model);

            var throttleKey = (model.UserName ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
            var lockLeft = LockedMinutesLeft(throttleKey);
            if (lockLeft > 0)
            {
                ModelState.AddModelError(string.Empty,
                    "\u767b\u5f55\u5931\u8d25\u6b21\u6570\u8fc7\u591a\uff0c\u8bf7 " + lockLeft + " \u5206\u949f\u540e\u91cd\u8bd5\u3002");   // 登录失败次数过多，请 N 分钟后重试。
                return View(model);
            }

            // 用户表：t_ERP_UserInfo（Account=登录账号, PassWord=MD5, UserName=显示名, RoleID, Status）
            t_ERP_UserInfo user;
            try
            {
                user = _db.t_ERP_UserInfo.FirstOrDefault(u => u.Account == model.UserName);
            }
            catch (Npgsql.NpgsqlException)
            {
                // 数据库连接失败（如网络不通、SQL 服务未启动）：给友好提示而非 500 崩溃。
                // ⚠ 2026-09-13 实测：本分支在「v_PMS_ICItem 基表被误删」时也会命中——
                // EF6 首次查询触发 ObjectContext 初始化，mapping 涉及坏视图即抛 EntityException。
                ModelState.AddModelError("", "系统数据库连接失败，请联系系统管理员。");
                return View(model);
            }

            if (user == null)
            {
                NoteFailure(throttleKey);
                _logger.LogInformation("Login failed: unknown account {Account} from {IP}",
                    model.UserName, HttpContext.Connection.RemoteIpAddress);
                ModelState.AddModelError(string.Empty, InvalidCredentialMessage);
                return View(model);
            }
            if (user.Status == 0)
            {
                // Deliberately the same message as a wrong password -- see InvalidCredentialMessage.
                NoteFailure(throttleKey);
                _logger.LogWarning("Login rejected: account {Account} is disabled (Status=0), IP {IP}",
                    user.Account, HttpContext.Connection.RemoteIpAddress);
                ModelState.AddModelError(string.Empty, InvalidCredentialMessage);
                return View(model);
            }
            if (!PasswordEquals(user.PassWord, model.Password))
            {
                NoteFailure(throttleKey);
                _logger.LogInformation("Login failed: wrong password for account {Account} from {IP}",
                    user.Account, HttpContext.Connection.RemoteIpAddress);
                ModelState.AddModelError(string.Empty, InvalidCredentialMessage);
                return View(model);
            }
            ClearFailures(throttleKey);

            bool isAdmin;
            try
            {
                isAdmin = _menuService.IsAdmin(user);
            }
            catch (Npgsql.NpgsqlException)
            {
                ModelState.AddModelError("", "系统数据库连接失败，请联系系统管理员。");
                return View(model);
            }
            bool isReadOnly;
            try
            {
                isReadOnly = user.RoleID > 3 && MenuService.IsReadOnlyRole(_db, user.RoleID);
            }
            catch (Npgsql.NpgsqlException)
            {
                ModelState.AddModelError("", "系统数据库连接失败，请联系系统管理员。");
                return View(model);
            }
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, string.IsNullOrEmpty(user.UserName) ? user.Account : user.UserName),
                new Claim("Account", user.Account ?? ""),
                new Claim("UserId", user.ID.ToString()),
                new Claim("RoleId", user.RoleID.ToString()),
                new Claim("IsAdmin", isAdmin ? "1" : "0"),
                new Claim("IsReadOnly", isReadOnly ? "1" : "0"),
                // Set when the account still carries a generated initial password. The global
                // filter blocks every other page until the password is changed, so an initial
                // password that was handed to (or intercepted by) somebody else stops working
                // the moment the real user changes it.
                new Claim(MustChangePwdClaim, user.PwdMustChange ? "1" : "0"),
                // Stamp the ticket with a "security stamp" (password hash + status + role).
                // The global filter compares it against the row on every request, so changing
                // the password (here, in SysUser, or from the old WinForms client) invalidates
                // every other session; and DISABLING the account or changing its role in
                // SysUser now kills the session too -- previously a disabled or demoted user
                // kept working for up to 8h on a stale ticket.
                new Claim(PwdStampClaim, SecurityStamp(user))
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            if (user.PwdMustChange)
                return RedirectToAction(nameof(ChangePassword));

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction("Index", "Home");
        }

        /// <summary>Claim carrying the "must change password" flag. Kept in one place because
        /// the filter, the login action and the change action all have to agree on it.</summary>
        public const string MustChangePwdClaim = "MustChangePwd";

        /// <summary>Claim carrying the password hash the ticket was issued under; the global
        /// filter uses it to drop sessions that outlived a password change. Kept here because
        /// login, change-password and the filter all have to agree on the name.</summary>
        public const string PwdStampClaim = "PwdStamp";

        /// <summary>
        /// 安全戳 = 口令哈希 + 账户状态 + 角色。比单纯的口令哈希多覆盖两件事：停用账户
        /// （Status=0）与角色变更（RoleID）也会让旧票据立即失效，而不用等 8 小时滑动过期。
        /// 修改此公式必须同步修改 MustChangePasswordAuthorizationFilter.StampMatches。
        /// </summary>
        internal static string SecurityStamp(t_ERP_UserInfo u)
            => (u.PassWord ?? "") + "|" + u.Status + "|" + u.RoleID;

        // ---- brute-force throttle -------------------------------------------------
        // In-memory and self-expiring: a wrong-password streak costs a few minutes at
        // most and can never permanently lock anyone out, and the database is never
        // touched. Keyed by account+IP so one attacker cannot lock out other users.
        // Scope note: this covers a single Kestrel process (how this app is deployed);
        // a multi-instance farm would need a shared cache instead.
        private const int MaxFailures = 5;
        private const int LockMinutes = 5;
        private static readonly Dictionary<string, FailCounter> Throttle =
            new Dictionary<string, FailCounter>(StringComparer.Ordinal);
        private static readonly object ThrottleLock = new object();

        private sealed class FailCounter { public int Fails; public DateTime LastFail; }

        /// <summary>Minutes left before this account+IP may try again (0 = not locked).</summary>
        private static int LockedMinutesLeft(string key)
        {
            lock (ThrottleLock)
            {
                FailCounter c;
                if (!Throttle.TryGetValue(key, out c) || c.Fails < MaxFailures) return 0;
                var left = (int)Math.Ceiling((c.LastFail.AddMinutes(LockMinutes) - DateTime.Now).TotalMinutes);
                if (left <= 0) { c.Fails = 0; return 0; }   // window elapsed, let them try again
                return left;
            }
        }

        private static void NoteFailure(string key)
        {
            lock (ThrottleLock)
            {
                FailCounter c;
                if (!Throttle.TryGetValue(key, out c)) { c = new FailCounter(); Throttle[key] = c; }
                // A stale streak must not count towards a new lockout.
                if (c.LastFail.AddMinutes(LockMinutes) < DateTime.Now) c.Fails = 0;
                c.Fails++;
                c.LastFail = DateTime.Now;
                // Garbage-collect entries whose lockout window has long passed so the dictionary
                // doesn't grow unbounded on a long-running process. Walk once per write; the
                // amortised cost is tiny (Throttle size ~ number of distinct keys ever throttled).
                var now = DateTime.Now;
                var dead = new List<string>();
                foreach (var kv in Throttle)
                {
                    if (kv.Value.LastFail.AddMinutes(LockMinutes * 2) < now) dead.Add(kv.Key);
                }
                foreach (var d in dead) Throttle.Remove(d);
            }
        }

        private static void ClearFailures(string key)
        {
            lock (ThrottleLock) { Throttle.Remove(key); }
        }

        // GET /Account/ChangePassword
        [HttpGet]
        [Authorize]
        public IActionResult ChangePassword()
        {
            ViewBag.Forced = MustChangePassword(out _);
            return View(new ChangePasswordViewModel());
        }

        // POST /Account/ChangePassword
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            bool forced = MustChangePassword(out int userId);
            ViewBag.Forced = forced;

            if (!ModelState.IsValid) return View(model);
            if (userId <= 0) return RedirectToAction(nameof(Login));

            var user = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == userId);
            if (user == null) return RedirectToAction(nameof(Login));

            // The old password is re-checked server side: without it, a stolen session cookie
            // (or a walked-away terminal) would be enough to take over the account.
            if (!PasswordEquals(user.PassWord, model.OldPassword))
            {
                ModelState.AddModelError(nameof(model.OldPassword),
                    "\u5f53\u524d\u5bc6\u7801\u4e0d\u6b63\u786e\u3002");   // 当前密码不正确。
                return View(model);
            }
            if (PasswordEquals(user.PassWord, model.NewPassword))
            {
                ModelState.AddModelError(nameof(model.NewPassword),
                    "\u65b0\u5bc6\u7801\u4e0d\u80fd\u4e0e\u5f53\u524d\u5bc6\u7801\u76f8\u540c\u3002");   // 新密码不能与当前密码相同。
                return View(model);
            }

            user.PassWord = PasswordHelper.Md5(model.NewPassword);
            user.PwdMustChange = false;
            _db.SaveChanges();

            // Re-issue the cookie without the flag, otherwise the user stays locked on this page
            // until the cookie expires.
            var kept = User.Claims
                .Where(c => c.Type != MustChangePwdClaim && c.Type != PwdStampClaim)
                .Select(c => new Claim(c.Type, c.Value))
                .ToList();
            kept.Add(new Claim(MustChangePwdClaim, "0"));
            // Re-stamp with the NEW security stamp: this session stays signed in, while every
            // other ticket (other device, other browser) now fails the filter's stamp check.
            kept.Add(new Claim(PwdStampClaim, SecurityStamp(user)));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(kept, CookieAuthenticationDefaults.AuthenticationScheme)));

            TempData["Success"] = "\u5bc6\u7801\u4fee\u6539\u6210\u529f\u3002";   // 密码修改成功。
            return RedirectToAction("Index", "Home");
        }

        /// <summary>True when the signed-in user still has to change the generated password.</summary>
        private bool MustChangePassword(out int userId)
        {
            userId = 0;
            int.TryParse(User.FindFirst("UserId")?.Value, out userId);
            return User.HasClaim(c => c.Type == MustChangePwdClaim && c.Value == "1");
        }

        /// <summary>
        /// 密码校验：与旧系统一致（Rnd.MD5 → 32 位小写 hex）。
        /// 兼容历史数据的两种常见变形：大写 hex、16 位短哈希（32 位的第 9~24 位）。
        ///
        /// 取舍说明（2026-09-09 复审后确认保留）：16 位短哈希只有 64 bit，碰撞面远大于
        /// 32 位，长期应通过"强制用户重置一次口令"把存量收敛为 32 位存储，再删掉这个
        /// 分支。现在不敢删：库里仍可能有旧 WinForms 写下的 16 位口令，一删就是大面积
        /// 登录不了。清理前需先统计 `LEN(PassWord)=16` 的账号数。
        /// </summary>
        private static bool PasswordEquals(string stored, string input)
        {
            if (string.IsNullOrEmpty(stored) || input == null) return false;

            var md5 = PasswordHelper.Md5(input);                 // 32 位小写
            if (string.Equals(stored, md5, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(stored, md5.Substring(8, 16), System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        // GET /Account/SessionExpired
        // The global filter sees a ticket whose password stamp no longer matches the row
        // (password changed on another device, reset by an admin, or changed in the old
        // WinForms client). It cannot sign out from the authorization stage -- that stage is
        // synchronous -- so it redirects here to kill the stale cookie.
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> SessionExpired()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            TempData["Error"] = "\u5bc6\u7801\u5df2\u53d8\u66f4\uff0c\u8bf7\u91cd\u65b0\u767b\u5f55\u3002";   // 密码已变更，请重新登录。
            return RedirectToAction(nameof(Login));
        }
    }
}
