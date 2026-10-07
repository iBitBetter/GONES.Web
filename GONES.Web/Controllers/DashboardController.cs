using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GONES.Model.Pg;
using GONES.Web.Services;

namespace GONES.Web.Controllers
{
    /// <summary>
    /// 控制台大屏 —— 生产 MES 数据看板。
    /// 数据构建见 <see cref="DashboardData"/>（静态共用，首页 Home/Index 亦复用同一份口径）。
    /// /Dashboard 为独立大屏页（Layout=null，适合投屏/kiosk 直链）；
    /// /Dashboard/Data 为 30s 自动刷新的 JSON 数据源。
    /// 登录用户均可查看（只读概览，[Authorize] 即够）。
    /// </summary>
    [Authorize]
    [Microsoft.AspNetCore.Mvc.NonValidation]
    public class DashboardController : Controller
    {
        private readonly GonesPgDbContext _db;
        public DashboardController(GonesPgDbContext db) => _db = db;

        // GET /Dashboard —— 独立全屏大屏页
        public IActionResult Index()
        {
            var vm = DashboardData.Build(_db);
            ViewBag.Title = "生产 MES 数据看板";
            return View(vm);
        }

        // GET /Dashboard/Data —— 自动刷新数据源（PascalCase，与首屏嵌入的 DASH 一致）
        public IActionResult Data()
        {
            var vm = DashboardData.Build(_db);
            var json = System.Text.Json.JsonSerializer.Serialize(vm);
            return Content(json, "application/json");
        }
    }
}
