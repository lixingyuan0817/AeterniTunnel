using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Config;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Cli.Tui;

// ═══════════════════════════════════════════════════════════
// Aeterni Tunnel CLI —— ATC 客户端
// 用法：
//   命令行模式：agent --server 1.2.3.4:7000 --token secret [--tls] --proxy tcp:25565:25566
//   配置文件模式：agent --config agent.toml [--tls]   （改配置自动热更新）
//   proxy 格式：tcp:本地端口:公网端口 | udp:本地端口:公网端口
//              http:本地端口:域名 | https:本地端口:域名
// ═══════════════════════════════════════════════════════════

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return;
}

if (args[0] == "agent")
{
    await RunAgentAsync(args[1..]);
}
else
{
    Console.WriteLine($"未知子命令：{args[0]}");
    PrintHelp();
}

static void PrintHelp()
{
    Console.WriteLine("""
        Aeterni Tunnel CLI —— ATC 客户端

        用法：
          Aeterni.Tunnel.Cli agent --server 主机:端口 --token 令牌 [选项] --proxy 类型:本地端口:公网端口
          Aeterni.Tunnel.Cli agent --config agent.toml [--tls]

        agent 选项：
          --server <host:port>   ATS 服务端地址（如 1.2.3.4:7000）
          --token <token>        认证令牌（与 ATS 一致）
          --tls                  启用 TLS 加密传输
          --proxy <spec>         隧道定义，可多次指定（见下）
          --config <file>        配置文件模式（agent.toml，支持热更新）
          --tui                  终端界面（实时状态表 + 日志窗口）
          -h, --help             帮助

        proxy 格式：
          tcp:本地端口:公网端口       TCP 隧道（如 tcp:25565:25566）
          udp:本地端口:公网端口       UDP 隧道（如 udp:19132:19133）
          http:本地端口:域名         HTTP vhost（如 http:8080:web.example.com）
          https:本地端口:域名        HTTPS vhost（SNI 路由）
        """);
}

async Task RunAgentAsync(string[] args)
{
    var server = GetValue(args, "--server");
    var token = GetValue(args, "--token");
    var useTls = args.Contains("--tls");
    var configPath = GetValue(args, "--config");

    AgentOptions options;
    List<ProxyDefinition> proxies;

    if (configPath is not null)
    {
        var cfg = ConfigLoader.LoadAgentConfig(configPath);
        if (cfg is null)
        {
            Console.Error.WriteLine($"[agent] 配置文件解析失败：{configPath}");
            return;
        }
        options = ConfigLoader.ToAgentOptions(cfg);
        if (useTls) options = options with { UseTls = true };
        proxies = ConfigLoader.ToProxyDefinitions(cfg);
        Console.WriteLine($"[agent] 配置文件模式：{configPath}（{proxies.Count} 条隧道，修改文件自动热更新）");
    }
    else
    {
        if (server is null || token is null)
        {
            Console.Error.WriteLine("[agent] 缺少参数：--server 和 --token（或使用 --config）");
            PrintHelp();
            return;
        }
        var (host, port) = ParseHostPort(server);
        options = new AgentOptions(host, port, token, ClientId: "", UseTls: useTls);
        proxies = ParseProxies(args);
        if (proxies.Count == 0)
        {
            Console.Error.WriteLine("[agent] 未指定任何隧道：--proxy tcp:25565:25566（或使用 --config）");
            return;
        }
    }

    await using var agent = new AgentHost(options);

    // 两种模式下都需要先注册隧道（AgentTui 内部 StartAsync 前）
    foreach (var p in proxies)
        agent.AddProxy(p);

    var useTui = args.Contains("--tui");

    if (useTui)
    {
        // TUI 模式：配置文件热更新依然生效，界面由 AgentTui 全权接管（内部 Start/Stop）
        if (configPath is not null)
            WatchAndReload(agent, configPath);
        await AgentTui.RunAsync(agent, CancellationToken.None);
        return;
    }

    // 纯日志模式
    agent.LogLine += m => Console.WriteLine($"[agent] {m}");
    agent.ProxyRegistered += (id, ok, msg) =>
        Console.WriteLine($"[agent] 隧道 {id} 注册{(ok ? "成功" : "失败")}：{msg ?? ""}");

    Console.WriteLine($"[agent] 连接 {options.ServerAddr}:{options.ServerPort}{(options.UseTls ? "（TLS）" : "")}，Ctrl+C 退出");
    await agent.StartAsync();

    // 配置文件模式：监视变更 → 热更新
    if (configPath is not null)
    {
        WatchAndReload(agent, configPath);
    }

    await WaitForCancellationAsync();
    Console.WriteLine("[agent] 正在退出…");
    await agent.StopAsync();
}

/// <summary>监听配置文件变更，延迟去抖后热更新隧道列表</summary>
static void WatchAndReload(AgentHost agent, string configPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
    var file = Path.GetFileName(configPath);
    var timer = new System.Threading.Timer(_ => { });

    var watcher = new FileSystemWatcher(dir, file)
    {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        EnableRaisingEvents = true,
    };

    watcher.Changed += (_, _) => timer.Change(500, Timeout.Infinite);   // 500ms 去抖

    timer = new System.Threading.Timer(async _ =>
    {
        try
        {
            var cfg = ConfigLoader.LoadAgentConfig(configPath);
            if (cfg is null) return;
            var newProxies = ConfigLoader.ToProxyDefinitions(cfg);
            var removed = await agent.ReloadAsync(newProxies);
            Console.WriteLine($"[agent] 配置热更新完成：{newProxies.Count} 条隧道" +
                              (removed.Count > 0 ? $"，移除 {string.Join(", ", removed)}" : ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[agent] 热更新失败：{ex.Message}");
        }
    });
}

/// <summary>等待 Ctrl+C / 进程退出信号</summary>
static Task WaitForCancellationAsync()
{
    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
    return tcs.Task;
}

static string? GetValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static (string Host, int Port) ParseHostPort(string spec)
{
    var idx = spec.LastIndexOf(':');
    if (idx <= 0 || !int.TryParse(spec[(idx + 1)..], out var port))
        throw new ArgumentException($"无效的主机:端口：{spec}");
    return (spec[..idx], port);
}

static List<ProxyDefinition> ParseProxies(string[] args)
{
    var list = new List<ProxyDefinition>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] != "--proxy" || i + 1 >= args.Length) continue;
        var spec = args[i + 1];
        var parts = spec.Split(':');
        if (parts.Length < 3) { Console.Error.WriteLine($"[agent] 无效的 proxy：{spec}"); continue; }

        var type = parts[0];
        var localPort = int.Parse(parts[1]);
        var name = $"p{list.Count}";

        if (type is "tcp" or "udp")
        {
            var remotePort = int.Parse(parts[2]);
            list.Add(new ProxyDefinition(name,
                type == "tcp" ? LinkType.Tcp : LinkType.Udp,
                "127.0.0.1", localPort, RemotePort: remotePort));
        }
        else if (type is "http" or "https")
        {
            var domain = parts[2];
            list.Add(new ProxyDefinition(name,
                type == "http" ? LinkType.Http : LinkType.Https,
                "127.0.0.1", localPort, Domain: domain));
        }
        else
        {
            Console.Error.WriteLine($"[agent] 未知隧道类型：{type}（支持 tcp/udp/http/https）");
        }
    }
    return list;
}
