using Aeterni.Tunnel.Engine.Config;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Web.Auth;
using Aeterni.Tunnel.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// --reset-token：生成新 webToken 写回 server.toml，打印明文后退出（不启动 Web）
if (args.Contains("--reset-token"))
{
    var resetPath = Path.Combine(builder.Environment.ContentRootPath, "server.toml");
    var (plain, salt, hash) = AeterniWebAuthService.Generate();
    var cfg = ConfigLoader.Load(resetPath) ?? new ServerConfig();
    cfg.WebToken = Convert.ToHexString(hash);
    cfg.WebTokenSalt = Convert.ToHexString(salt);
    ConfigLoader.SaveServer(resetPath, cfg);
    Console.WriteLine($"[Aeterni] 已重置 webToken（写入 {resetPath}）：{plain}");
    Console.WriteLine("[Aeterni] 请立即保存该明文，此后仅以加盐哈希形式存在于配置中。");
    Console.WriteLine("[Aeterni] 提示：勿用 dotnet watch 运行 --reset-token（应用会正常退出）；重置后请正常启动（dotnet watch run 不带此参数）。");
    return;
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Aeterni Web 登录认证：Cookie 方案（webToken 校验见 AeterniWebAuthService）
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Aeterni.WebAuth";
        options.LoginPath = "/login";         // 未认证访问受保护页 → 跳登录
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState(); // 电路内 AuthenticationState 感知（AuthorizeRouteView）
builder.Services.AddHttpContextAccessor();

// 登录校验（token 来源解析见 BuildAuthService）
builder.Services.AddSingleton(sp => BuildAuthService(sp, builder.Environment.ContentRootPath));

// 内嵌 ATS 引擎：读 server.toml → 启动 ServerHost（ATC 通过 bindPort 连接注册隧道）
builder.Services.AddSingleton(sp => BuildServerHost(sp, builder.Environment.ContentRootPath));

// 管理端数据服务（真实快照 + 速率差分）
builder.Services.AddSingleton<Aeterni.Tunnel.Web.Status.AeterniServerStatusService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();   // 默认所有组件页需登录；公开页（/login、/logout、/examples/login、/not-found）用组件级 [AllowAnonymous]

// 预热：应用启动即启动内嵌 ATS（而非等首个管理页请求）
_ = app.Services.GetRequiredService<Aeterni.Tunnel.Engine.Hosting.ServerHost>();

app.Run();

// webToken 来源解析：env(AETERNI_WEB_TOKEN) > server.toml(加盐哈希) > 首启自动生成写回
// （不提供 appsettings 明文兜底：明文 token 易被误提交，首启自动生成已完全兜底）
static AeterniWebAuthService BuildAuthService(IServiceProvider sp, string contentRoot)
{
    var logger = sp.GetRequiredService<ILogger<AeterniWebAuthService>>();
    var serverToml = Path.Combine(contentRoot, "server.toml");

    // 1) 环境变量覆盖（最高优先级，不落盘）
    var envToken = Environment.GetEnvironmentVariable("AETERNI_WEB_TOKEN");
    if (!string.IsNullOrEmpty(envToken))
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new AeterniWebAuthService(AeterniWebAuthService.EnvSource, salt, AeterniWebAuthService.Hash(envToken, salt), serverToml, logger);
    }

    // 2) server.toml：webToken + webTokenSalt（加盐哈希落盘）
    var cfg = ConfigLoader.Load(serverToml);
    if (cfg is not null && !string.IsNullOrEmpty(cfg.WebToken) && !string.IsNullOrEmpty(cfg.WebTokenSalt))
    {
        return new AeterniWebAuthService(AeterniWebAuthService.FileSource,
            Convert.FromHexString(cfg.WebTokenSalt), Convert.FromHexString(cfg.WebToken), serverToml, logger);
    }

    // 3) 首启：随机生成并写回 server.toml（持久化；明文仅本次日志可见）
    var (plain, s, h) = AeterniWebAuthService.Generate();
    var serverCfg = cfg ?? new ServerConfig();
    serverCfg.WebToken = Convert.ToHexString(h);
    serverCfg.WebTokenSalt = Convert.ToHexString(s);
    ConfigLoader.SaveServer(serverToml, serverCfg);
    logger.LogWarning("未配置 webToken，已生成并写入 {Path}（明文仅本次打印）：{Plain}", serverToml, plain);
    return new AeterniWebAuthService(AeterniWebAuthService.FileSource, s, h, serverToml, logger);
}

// 内嵌 ATS：加载 server.toml 启动 ServerHost；ATS token 为空时生成并写回（ATC 登录凭据）
static ServerHost BuildServerHost(IServiceProvider sp, string contentRoot)
{
    var logger = sp.GetRequiredService<ILogger<ServerHost>>();
    var serverToml = Path.Combine(contentRoot, "server.toml");
    var cfg = ConfigLoader.Load(serverToml) ?? new ServerConfig();

    if (string.IsNullOrEmpty(cfg.Token))
    {
        var newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cfg.Token = newToken;
        ConfigLoader.SaveServer(serverToml, cfg);
        logger.LogWarning("ATS token 为空，已生成并写入 {Path}（ATC 在 agent.toml 中配置同一 token）：{Token}", serverToml, newToken);
    }

    var host = new ServerHost();
    host.LogLine += line => logger.LogInformation("[ATS] {Line}", line);
    try
    {
        host.Start(ConfigLoader.ToHostOptions(cfg));
        logger.LogInformation("ATS 已启动：bindPort={BindPort}", cfg.BindPort);
    }
    catch (Exception ex)
    {
        // 端口被占用等：不拖垮 Web，管理页显示 ATS 未运行
        logger.LogError(ex, "ATS 启动失败（bindPort={BindPort} 可能被占用），请修改 server.toml 的 bindPort", cfg.BindPort);
    }
    return host;
}
