using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GONES.Web.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly GonesPgDbContext _db;
        private readonly MenuService _menuService;
        public HomeController(GonesPgDbContext db, MenuService menuService)
        {
            _db = db;
            _menuService = menuService;
        }

        // 控制台首页 —— 门店系统风格：生产数据统计(日/周/月/季/年) + 快捷入口(按角色+账号权限) + 报表统计
        public IActionResult Index(string p)
        {
            ViewBag.Title = "控制台首页";
            var isAdmin = User.HasClaim(c => c.Type == "IsAdmin" && c.Value == "1");

            // 与侧边栏菜单同源解析当前账号：管理员=全部菜单，普通用户=角色授权菜单。
            // 只有普通用户需要按 UserId 回查用户行（管理员直接看全部）。
            t_ERP_UserInfo current = null;
            if (!isAdmin && int.TryParse(User.FindFirst("UserId")?.Value, out int uid))
                current = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == uid);

            // 可访问路由集合 → 供快捷入口按权限过滤（无权限的入口不显示）。
            var allowedUrls = _menuService.GetAccessibleUrls(_db, isAdmin, current);

            var vm = HomeStatsData.Build(_db, p, isAdmin, User.Identity?.Name ?? "用户", allowedUrls);
            return View(vm);
        }
    }
}
