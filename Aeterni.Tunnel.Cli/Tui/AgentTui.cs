using System.Collections.Concurrent;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;
using Spectre.Console;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// ATC 交互式 TUI：Figlet 品牌 + 连接状态 + 隧道状态表 + 实时日志窗 + 命令输入。
/// 命令：add（添加隧道）/ remove（移除）/ list（刷新）/ help / quit。
/// 连接在后台进行（握手失败自动重连），界面立即显示。
/// </summary>
public static class AgentTui
{
    private const int LogKeepLines = 18;

    public static async Task RunAsync(AgentHost agent, CancellationToken ct)
    {
        // TUI 需要真实终端：输出被重定向（管道/文件）时无法渲染，明确提示后退出
        if (Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("[agent] TUI 需要真实终端（当前输出被重定向）。请使用 --config 配置文件模式。");
            await agent.StartAsync(ct);
            await agent.StopAsync();
            return;
        }

        // 隧道注册结果（ProxyRegistered 事件 → ProxyId → (成功?, 信息)）
        var registered = new ConcurrentDictionary<string, (bool Ok, string? Msg)>();
        agent.ProxyRegistered += (id, ok, msg) => registered[id] = (ok, msg);

        // 日志环形缓冲（含时间戳）
        var logs = new ConcurrentQueue<string>();
        agent.LogLine += s =>
        {
            logs.Enqueue($"{DateTime.Now:HH:mm:ss} {s}");
            while (logs.Count > LogKeepLines) logs.TryDequeue(out _);
        };

        // 后台连接：StartAsync 内部处理失败（握手超时/拒绝 → 后台重连循环），不阻塞交互
        _ = Task.Run(async () =>
        {
            try { await agent.StartAsync(ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[agent] 启动异常：{ex.Message}"); }
        });

        // 命令循环
        var running = true;
        while (running && !ct.IsCancellationRequested)
        {
            Render(agent, registered, logs);
            var cmd = AnsiConsole.Prompt(new TextPrompt<string>("[bold green]ats[/]›").AllowEmpty());
            running = await ExecuteAsync(agent, cmd, registered);
        }

        AnsiConsole.MarkupLine("[grey]正在退出…[/]");
        await agent.StopAsync();
    }

    // ═════════ 渲染 ═════════

    private static void Render(
        AgentHost agent,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered,
        ConcurrentQueue<string> logs)
    {
        AnsiConsole.Clear();

        // ① 品牌 + 连接状态
        AnsiConsole.Write(new FigletText("AETERNI").Color(Color.Green));
        var state = agent.IsConnected
            ? "[green]● 已连接[/]"
            : "[yellow]○ 重连中[/]";
        AnsiConsole.MarkupLine($"  [dim]TUNNEL · Agent[/]   {state}   隧道 [bold]{agent.Proxies.Count}[/] 条");

        // ② 隧道状态表
        var table = new Table
        {
            Border = TableBorder.Rounded,
            Expand = true,
        };
        table.Title = new TableTitle(" 隧道 ");
        table.AddColumn(new TableColumn("名称").Centered());
        table.AddColumn(new TableColumn("类型").Centered());
        table.AddColumn(new TableColumn("远程地址"));
        table.AddColumn(new TableColumn("↑ 累计").Centered());
        table.AddColumn(new TableColumn("↓ 累计").Centered());
        table.AddColumn(new TableColumn("状态").Centered());

        foreach (var p in agent.Proxies)
        {
            (bool Ok, string? Msg) reg = registered.TryGetValue(p.ProxyId, out var r) ? r : (false, null);
            (long Up, long Down) traffic = agent.GetTraffic().TryGetValue(p.ProxyId, out var t) ? t : (0L, 0L);

            var remote = p.RemotePort is not null ? $"0.0.0.0:{p.RemotePort}" : p.Domain ?? "-";
            var status = !agent.IsConnected
                ? "[grey]○ 未连接[/]"
                : reg.Ok
                    ? "[green]● 在线[/]"
                    : reg.Msg is not null
                        ? $"[red]● 失败[/] [dim]{Markup.Escape(reg.Msg)}[/]"
                        : "[grey]○ 注册中[/]";

            table.AddRow(
                Markup.Escape(p.ProxyId),
                p.LinkType.ToString().ToLowerInvariant(),
                Markup.Escape(remote),
                $"↑{Format.Bytes(traffic.Up)}",
                $"↓{Format.Bytes(traffic.Down)}",
                status);
        }
        AnsiConsole.Write(table);

        // ③ 日志窗
        var logPanel = new Panel(string.Join("\n", logs.Select(Markup.Escape)))
        {
            Header = new PanelHeader(" 日志 "),
            Border = BoxBorder.Rounded,
        };
        AnsiConsole.Write(logPanel);

        // ④ 命令提示
        AnsiConsole.MarkupLine("[dim]help 查看命令 · add tcp:25565:6071 添加隧道 · quit 退出[/]");
    }

    // ═════════ 命令 ═════════

    private static async Task<bool> ExecuteAsync(
        AgentHost agent, string cmd,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered)
    {
        var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return true;

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                AnsiConsole.MarkupLine("""
                    [bold]命令：[/]
                      [cyan]add <类型>:<本地端口>:<公网端口|域名>[/]   添加隧道
                         tcp:25565:6071 · udp:19132:6072 · http:8080:web.example.com · https:8443:web.example.com
                      [cyan]remove <名称>[/]       移除隧道（如 remove p0）
                      [cyan]list[/] / [cyan]status[/]   刷新状态
                      [cyan]quit[/] / [cyan]exit[/]     退出
                    """);
                Pause();
                return true;

            case "add":
                await AddTunnelAsync(agent, parts[1..]);
                Pause();
                return true;

            case "remove":
            case "rm":
                await RemoveTunnelAsync(agent, parts.ElementAtOrDefault(1) ?? "");
                Pause();
                return true;

            case "list":
            case "status":
                return true;   // 重绘即刷新

            case "quit":
            case "exit":
            case "q":
                return false;

            default:
                AnsiConsole.MarkupLine($"[red]未知命令：{parts[0]}[/]（输入 [cyan]help[/] 查看）");
                Pause();
                return true;
        }
    }

    private static async Task AddTunnelAsync(AgentHost agent, string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]用法：add <类型>:<本地端口>:<公网端口|域名>[/]（如 [cyan]add tcp:25565:6071[/]）");
            return;
        }

        var def = ParseTunnelSpec(args[0], agent.Proxies.Count);
        if (def is null)
            return;

        // 等待该隧道注册结果（事件 + 超时）
        var tcs = new TaskCompletionSource<(bool Ok, string? Msg)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string, bool, string?> handler = null!;
        handler = (id, ok, msg) =>
        {
            if (id == def.ProxyId)
            {
                tcs.TrySetResult((ok, msg));
                agent.ProxyRegistered -= handler;
            }
        };
        agent.ProxyRegistered += handler;

        try
        {
            await agent.AddProxyAsync(def);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]添加失败：{Markup.Escape(ex.Message)}[/]");
            return;
        }

        // 等注册结果（连接中/重连时可能超时——加入待注册列表）
        (bool Ok, string? Msg) result;
        try
        {
            result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            AnsiConsole.MarkupLine($"[yellow]隧道 [bold]{def.ProxyId}[/] 已加入待注册列表[/]（当前未连接，连接成功后自动注册）");
            return;
        }

        AnsiConsole.MarkupLine(result.Ok
            ? $"[green]✔ 隧道 [bold]{def.ProxyId}[/] 添加成功：{Markup.Escape(result.Msg ?? "")}[/]"
            : $"[red]✘ 隧道 [bold]{def.ProxyId}[/] 注册失败：{Markup.Escape(result.Msg ?? "未知错误")}[/]");
    }

    private static async Task RemoveTunnelAsync(AgentHost agent, string proxyId)
    {
        if (string.IsNullOrWhiteSpace(proxyId))
        {
            AnsiConsole.MarkupLine("[red]用法：remove <名称>[/]（如 [cyan]remove p0[/]）");
            return;
        }
        await agent.RemoveProxyAsync(proxyId);
        AnsiConsole.MarkupLine($"[green]✔ 隧道 [bold]{Markup.Escape(proxyId)}[/] 已移除[/]");
    }

    /// <summary>解析隧道定义：tcp:25565:6071 / udp:19132:6072 / http:8080:web.example.com / https:8443:web.example.com</summary>
    private static ProxyDefinition? ParseTunnelSpec(string spec, int index)
    {
        var parts = spec.Split(':');
        if (parts.Length < 3)
        {
            AnsiConsole.MarkupLine($"[red]无效的隧道定义：{Markup.Escape(spec)}[/]");
            return null;
        }

        var type = parts[0];
        if (!int.TryParse(parts[1], out var localPort))
        {
            AnsiConsole.MarkupLine($"[red]无效的本地端口：{Markup.Escape(parts[1])}[/]");
            return null;
        }
        var name = $"p{index}";

        if (type is "tcp" or "udp")
        {
            if (!int.TryParse(parts[2], out var remotePort))
            {
                AnsiConsole.MarkupLine($"[red]无效的公网端口：{Markup.Escape(parts[2])}[/]");
                return null;
            }
            return new ProxyDefinition(name,
                type == "tcp" ? LinkType.Tcp : LinkType.Udp,
                "127.0.0.1", localPort, RemotePort: remotePort);
        }

        if (type is "http" or "https")
        {
            return new ProxyDefinition(name,
                type == "http" ? LinkType.Http : LinkType.Https,
                "127.0.0.1", localPort, Domain: parts[2]);
        }

        AnsiConsole.MarkupLine($"[red]未知隧道类型：{Markup.Escape(type)}[/]（支持 tcp/udp/http/https）");
        return null;
    }

    private static void Pause()
    {
        AnsiConsole.Markup("[dim]按回车继续…[/]");
        Console.ReadLine();
    }
}
