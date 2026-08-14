using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Config;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Cli.Tui;

// ═══════════════════════════════════════════════════════════
// Aeterni Tunnel CLI —— ATC 客户端
// 用法：
//   TUI 交互模式：aeterni-client --server 主机:端口 --token 令牌 [--tls] [--tui]
//                  （不填 --server/--token 会交互式提示；进入界面后可用命令添加/管理隧道）
//   配置文件模式：aeterni-client --config agent.toml [--tls]
//                  （读 [[tunnels]]，纯日志运行，改配置自动热更新）
// ═══════════════════════════════════════════════════════════

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return;
}

var configPath = GetValue(args, "--config");
if (configPath is not null)
{
    await RunConfigModeAsync(configPath, args.Contains("--tls"));
    return;
}

// ── TUI 交互模式（默认）──
// 服务端信息：命令行 --server/--token，未提供则交互式提示
var server = GetValue(args, "--server");
var token = GetValue(args, "--token");

if (server is null || token is null)
{
    server ??= Prompt("ATS 服务端地址（host:port）");
    token ??= Prompt("认证令牌");
}

var (host, port) = ParseHostPort(server);
var useTls = args.Contains("--tls");

await using var agent = new AgentHost(new AgentOptions(host, port, token, ClientId: "", UseTls: useTls));
await AgentTui.RunAsync(agent, CancellationToken.None);

/// <summary>终端提示输入（无 Spectre 依赖）</summary>
static string Prompt(string label)
{
    Console.Write($"{label}：");
    return Console.ReadLine()?.Trim() ?? "";
}

static void PrintHelp()
{
    Console.WriteLine("""
        Aeterni Tunnel CLI —— ATC 客户端

        用法：
          aeterni-client --server 主机:端口 --token 令牌 [--tui]
            交互式界面：隧道列表 + 日志 + 指令（atc.Tunnel.Add(...) 等）
          aeterni-client --config agent.toml
            配置文件模式：读 [[tunnels]] 纯日志运行，改配置自动热更新

        选项：
          --server <host:port>   ATS 服务端地址（不填则界面内提示）
          --token <token>        认证令牌（不填则界面内提示）
          --config <file>        配置文件模式（非交互）
          --tls                  启用 TLS 加密传输
          -h, --help             帮助

        TUI 内命令：
          add <类型>:<本地端口>:<公网端口|域名>   添加隧道（如 add tcp:25565:6071 / add http:8080:web.example.com）
          remove <名称>                          移除隧道
          list / status                          刷新状态
          quit / exit                            退出
        """);
}

/// <summary>配置文件模式：加载 agent.toml → 连接 → 热更新 → 等 Ctrl+C</summary>
async Task RunConfigModeAsync(string configPath, bool useTls)
{
    var cfg = ConfigLoader.LoadAgentConfig(configPath);
    if (cfg is null)
    {
        Console.Error.WriteLine($"[agent] 配置文件解析失败：{configPath}");
        return;
    }
    var options = ConfigLoader.ToAgentOptions(cfg);
    if (useTls) options = options with { UseTls = true };
    var tunnels = ConfigLoader.ToProxyDefinitions(cfg);
    Console.WriteLine($"[agent] 配置文件模式：{configPath}（{tunnels.Count} 条隧道，修改文件自动热更新）");

    await using var agent = new AgentHost(options);
    foreach (var t in tunnels)
        agent.AddProxy(t);

    agent.LogLine += m => Console.WriteLine($"[agent] {m}");
    agent.ProxyRegistered += (id, ok, msg) =>
        Console.WriteLine($"[agent] 隧道 {id} 注册{(ok ? "成功" : "失败")}：{msg ?? ""}");

    Console.WriteLine($"[agent] 连接 {options.ServerAddr}:{options.ServerPort}{(options.UseTls ? "（TLS）" : "")}，Ctrl+C 退出");
    await agent.StartAsync();

    WatchAndReload(agent, configPath);
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
            var newTunnels = ConfigLoader.ToProxyDefinitions(cfg);
            var removed = await agent.ReloadAsync(newTunnels);
            Console.WriteLine($"[agent] 配置热更新完成：{newTunnels.Count} 条隧道" +
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
