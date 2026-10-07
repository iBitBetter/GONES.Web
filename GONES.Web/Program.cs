using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Text;
using GONES.Model.Pg;
using Microsoft.EntityFrameworkCore;
using GONES.Web.Filters;
using GONES.Web.Services;
using GONES.Web.Data.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;

// (EF6 SQL Server provider registration removed for the PostgreSQL fork.)

// Furion: .Inject() on the builder initializes App.Configuration/Environment
// BEFORE any service registration that touches App.Settings (required).
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args).Inject();

// Furion integration: AddInject wires up friendly-exception middleware, Swagger docs,
// unified result support, and auto-DI for ISingleton/IScoped/ITransient markers.
// MustChangePasswordAuthorizationFilter 锁定“待改密”用户于改密页，直到初始口令被改掉。
// MenuAccessFilter = 集中式菜单权限闸门（唯一真相源 t_erp_menu.route），取代原先散落在
// 各控制器 Action 内的手写 CanAccess 判断；只对实现 IMenuGuarded 的控制器生效。
builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add<MustChangePasswordAuthorizationFilter>();
    o.Filters.Add<MenuAccessFilter>();
}).AddInject();

// 旧 WinForms 用 Rnd.MD5 时 Encoding.Default 在中文 Windows 上是 GBK(936)。
// 注册 CodePages 才能产生相同字节（ASCII 密码如 "123" 在 UTF-8/GBK 下字节一致）。
// 注意：System.Text.Encoding.CodePages 命名空间与 System.Text.Encoding 类型同名，
// 在 C# 源码里用 using / global:: 都会触发 CS0426/CS0117 歧义，故用反射按字符串
// 类型名加载 Instance，彻底绕开编译期命名空间歧义。
var codePagesAsm = System.Reflection.Assembly.Load("System.Text.Encoding.CodePages");
var codePagesType = codePagesAsm?.GetType("System.Text.Encoding.CodePages.CodePagesEncodingProvider");
if (codePagesType != null)
{
    var codePagesInstance = codePagesType.GetProperty("Instance")?.GetValue(null);
    if (codePagesInstance is System.Text.EncodingProvider provider)
    {
        System.Text.Encoding.RegisterProvider(provider);
    }
}

// PostgreSQL (EF Core Code-First). Connection from env var GONES_PG_CONN (default 5432;
// sandbox/CI may override to 5433). Falls back to appsettings ConnectionStrings:GonesPg.
var pgConn = Environment.GetEnvironmentVariable("GONES_PG_CONN")
    ?? builder.Configuration.GetConnectionString("GonesPg")
    ?? "Host=127.0.0.1;Port=5432;Database=GONESERP;Username=postgres;Password=postgres";
builder.Services.AddDbContext<GonesPgDbContext>(options => options.UseNpgsql(pgConn), ServiceLifetime.Scoped);

// DepotScope (IScoped) resolves the current user's warehouse set from the request, so it needs
// access to HttpContext.User. AddHttpContextAccessor is not implied by AddControllersWithViews.
builder.Services.AddHttpContextAccessor();

// MenuService now implements Furion ISingleton — auto-registered by AddInject.
// (The old explicit builder.Services.AddSingleton<MenuService>() is no longer needed.)

// Cookie authentication — no external identity provider needed for an internal ERP.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        // SameAsRequest + UseForwardedHeaders：生产走 HTTPS 反代时请求协议被修正为 https，
        // Cookie 随之带 Secure；本地 HTTP 开发不受影响（SecurePolicy.Always 会让本地登录死循环）。
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
// FallbackPolicy（2026-09-19 加固）：此前授权完全依赖每个控制器手写 [Authorize]，
// 一旦新增控制器/视图组件忘记标注，整个模块就对匿名访客敞开且**不会有任何编译或运行提示**。
// 改为"默认必须登录"，登录/登出等少数入口用 [AllowAnonymous] 显式开洞，漏标即 fail-closed。
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// 反向代理（Caddy/Nginx/IIS/容器编排）会把真实客户端 IP 与协议放进 X-Forwarded-* 头。
// 不修协议 → SameAsRequest Cookie 判错、重定向生成 http:// 绝对地址；
// 不修 IP → 丢失审计来源。KnownProxies 清空：容器化部署时代理来自 Docker 网桥
// （非 loopback，默认不被信任会静默忽略转发头）；Kestrel 仅容器内网监听，不直接暴露公网。
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Clear();
});

var app = builder.Build();

// 启动自愈：若 GONESERP 库/表不存在，则自动建库(仅库缺失时)→建表→播种必备数据。
// 幂等（标记表存在即跳过）、只建不删、建表与播种同事务失败回滚。详见 DbInitializer。
await DbInitializer.InitializeAsync(pgConn);

// 必须位于管道最前，先于 UseInject/UseStaticFiles 修正请求上下文。
app.UseForwardedHeaders();

// Furion pipeline: UseInject registers the exception handler + Swagger doc,
// and internally calls UseRouting — do NOT call app.UseRouting() again.
app.UseInject();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
