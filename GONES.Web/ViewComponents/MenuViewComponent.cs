using Microsoft.AspNetCore.Mvc;
using System.Linq;
using GONES.Model.Pg;
using GONES.Web.Models;
using GONES.Web.Services;

namespace GONES.Web.ViewComponents
{
    /// <summary>
    /// 在 _Layout 侧边栏渲染当前用户的菜单树（由 t_ERP_Menu 动态生成）。
    /// 仅对登录用户调用；匿名用户走登录页布局，不会触发本组件。
    /// </summary>
    public class MenuViewComponent : ViewComponent
    {
        private readonly GonesPgDbContext _db;
        private readonly MenuService _menuService;
        public MenuViewComponent(GonesPgDbContext db, MenuService menuService)
        {
            _db = db;
            _menuService = menuService;
        }

        public IViewComponentResult Invoke()
        {
            var isAdmin = HttpContext.User?.FindFirst("IsAdmin")?.Value == "1";

            t_ERP_UserInfo user = null;
            if (!isAdmin)
            {
                // 管理员直接看全部菜单，无需再查库；普通用户才按角色菜单裁剪。
                var userIdStr = HttpContext.User?.FindFirst("UserId")?.Value;
                if (userIdStr != null && int.TryParse(userIdStr, out int uid))
                    user = _db.t_ERP_UserInfo.FirstOrDefault(u => u.ID == uid);
            }

            var tree = _menuService.GetMenuTree(_db, isAdmin, user);
            return View(tree);
        }
    }
}
