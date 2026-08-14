using System.Collections.Concurrent;
using System.Text;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;
using Spectre.Console;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// ATC 全屏交互式 TUI：占满终端，自适应尺寸。
/// 布局：上部隧道状态表 + 中部日志 + 底部命令输入行。
/// 实时刷新（后台 1s 覆盖重绘，无频闪），输入支持 Tab 补全命令，Ctrl+C 退出。
/// </summary>
public static class AgentTui
{
    private static readonly string[] Commands = ["add", "remove", "list", "status", "help", "quit", "exit"];

    public static async Task RunAsync(AgentHost agent, CancellationToken ct)
    {
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
            while (logs.Count > 64) logs.TryDequeue(out _);
        };

        // 后台连接（失败自动重连，不阻塞界面）
        _ = Task.Run(async () =>
        {
            try { await agent.StartAsync(ct); }
            catch (Exception ex) { Console.Error.WriteLine($"[agent] 启动异常：{ex.Message}"); }
        });

        // 隐藏光标（自绘界面）
        try { AnsiConsole.Cursor.Hide(); } catch (IOException) { }

        var input = new StringBuilder();
        var running = true;
        var lastRender = 0L;

        try
        {
            while (running && !ct.IsCancellationRequested)
            {
                // 定时刷新（1s）+ 处理按键
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            running = await ExecuteAsync(agent, input.ToString(), registered);
                            input.Clear();
                            RenderAll(agent, registered, logs, input.ToString());
                            break;
                        case ConsoleKey.Backspace when input.Length > 0:
                            input.Length--;
                            RenderInputLine(input.ToString());
                            break;
                        case ConsoleKey.Tab:
                            TabComplete(input);
                            RenderInputLine(input.ToString());
                            break;
                        case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                            running = false;
                            break;
                        default:
                            if (!char.IsControl(key.KeyChar))
                            {
                                input.Append(key.KeyChar);
                                RenderInputLine(input.ToString());
                            }
                            break;
                    }
                }
                else
                {
                    var now = Environment.TickCount64;
                    if (now - lastRender >= 1000)
                    {
                        lastRender = now;
                        RenderAll(agent, registered, logs, input.ToString());
                    }
                    Thread.Sleep(40);
                }
            }
        }
        finally
        {
            try { AnsiConsole.Cursor.Show(); } catch (IOException) { }
            Console.WriteLine();
        }

        await agent.StopAsync();
    }

    // ═════════ 渲染（全屏覆盖，无频闪） ═════════

    private static void RenderAll(
        AgentHost agent,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered,
        ConcurrentQueue<string> logs,
        string input)
    {
        var height = Format.TerminalHeight();
        var width = Format.TerminalWidth();

        AnsiConsole.Write("\x1b[H");   // 光标归位，覆盖重绘（不清屏 → 不闪）

        // ① 品牌 + 状态行
        var state = agent.IsConnected ? "[green]● 已连接[/]" : "[yellow]○ 重连中[/]";
        AnsiConsole.MarkupLine($"  [bold green]AETERNI[/] [dim]TUNNEL · Agent[/]  {state}  隧道 [bold]{agent.Proxies.Count}[/] 条");
        AnsiConsole.MarkupLine($"  [dim]{new string('─', Math.Max(20, width - 4))}[/]");

        // ② 隧道状态表（按剩余高度截断）
        var tableHeight = Math.Min(agent.Proxies.Count + 3, Math.Max(4, height - 8));
        RenderTable(agent, registered, tableHeight);

        // ③ 日志窗（填满剩余）
        var logRows = Math.Max(1, height - tableHeight - 6);
        var logLines = logs.TakeLast(logRows).ToArray();
        if (logLines.Length == 0) logLines = ["[dim]（等待日志…）[/]"];
        AnsiConsole.MarkupLine($"  [dim]{new string('─', Math.Max(20, width - 4))}[/]");
        foreach (var line in logLines)
        {
            var clipped = line.Length > width - 4 ? line[..(width - 4)] : line;
            AnsiConsole.MarkupLine($"  {Markup.Escape(clipped)}");
        }

        // 清除下方残留
        AnsiConsole.Write("\x1b[J");
        RenderInputLine(input);
    }

    private static void RenderTable(
        AgentHost agent,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered,
        int maxRows)
    {
        var rows = agent.Proxies.Take(Math.Max(0, maxRows - 2)).ToList();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("  [dim]（暂无隧道——输入 [cyan]add tcp:25565:6071[/] 添加）[/]");
            return;
        }

        // 表头
        AnsiConsole.MarkupLine("  [bold]名称[/]    [bold]类型[/]  [bold]远程地址[/]            [bold]↑ 流量[/]  [bold]↓ 流量[/]  [bold]状态[/]");
        foreach (var p in rows)
        {
            (bool Ok, string? Msg) reg = registered.TryGetValue(p.ProxyId, out var r) ? r : (false, null);
            (long Up, long Down) traffic = agent.GetTraffic().TryGetValue(p.ProxyId, out var t) ? t : (0L, 0L);
            var remote = p.RemotePort is not null ? $"0.0.0.0:{p.RemotePort}" : p.Domain ?? "-";
            var status = !agent.IsConnected
                ? "[grey]未连接[/]"
                : reg.Ok
                    ? "[green]● 在线[/]"
                    : reg.Msg is not null
                        ? $"[red]● 失败[/]"
                        : "[grey]○ 注册中[/]";

            AnsiConsole.MarkupLine($"  {Markup.Escape(p.ProxyId),-6}  {p.LinkType.ToString().ToLowerInvariant(),-5}  {Markup.Escape(remote),-20}  ↑{Format.Bytes(traffic.Up),-7}  ↓{Format.Bytes(traffic.Down),-7}  {status}");
        }
        if (rows.Count < agent.Proxies.Count)
            AnsiConsole.MarkupLine($"  [dim]… 还有 {agent.Proxies.Count - rows.Count} 条未显示[/]");
    }

    private static void RenderInputLine(string input)
    {
        var row = Format.TerminalHeight();
        AnsiConsole.Write($"\x1b[{row};1H\x1b[2K");
        var display = input.Length > 80 ? input[^80..] : input;
        AnsiConsole.Markup($"[bold green]ats›[/] {Markup.Escape(display)}");
    }

    // ═════════ 命令 ═════════

    private static async Task<bool> ExecuteAsync(
        AgentHost agent, string cmd,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered)
    {
        // ① C# 风格调用：atc.Tunnel.Add("mc", "tcp", "25565", "6071")
        if (TryParseAtcCall(cmd, out var obj, out var method, out var args))
            return await ExecuteAtcCallAsync(agent, obj, method, args, registered);

        // ② 旧命令兼容
        var parts = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return true;

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                WriteResult("[bold]命令：[/] atc.Tunnel.Add/Remove/List · atc.Status · atc.Quit（或旧式 add/remove/list/quit）");
                return true;

            case "add":
                await AddTunnelAsync(agent, parts[1..]);
                return true;

            case "remove":
            case "rm":
                await RemoveTunnelAsync(agent, parts.ElementAtOrDefault(1) ?? "");
                return true;

            case "list":
            case "status":
                return true;

            case "quit":
            case "exit":
            case "q":
                return false;

            default:
                WriteResult($"[red]未知命令：{Markup.Escape(parts[0])}[/]（输入 [cyan]atc.Help()[/] 查看）");
                return true;
        }
    }

    // ═════════ C# 风格 REPL（atc.* 方法调用，自研解析——AOT 友好） ═════════

    /// <summary>解析 atc.<对象>.<方法>("arg1", "arg2", ...) 调用</summary>
    private static bool TryParseAtcCall(string input, out string obj, out string method, out List<string> args)
    {
        obj = ""; method = ""; args = [];
        var trimmed = input.Trim().TrimEnd(';').Trim();
        var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^atc\.([\w.]+)\.(\w+)\s*\((.*)\)$");
        if (!m.Success)
            return false;
        obj = m.Groups[1].Value;
        method = m.Groups[2].Value;
        args = m.Groups[3].Value
            .Split(',')
            .Select(a => a.Trim().Trim('"', '\''))
            .Where(a => a.Length > 0)
            .ToList();
        return true;
    }

    private static async Task<bool> ExecuteAtcCallAsync(
        AgentHost agent, string obj, string method, List<string> args,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered)
    {
        switch ($"{obj}.{method}")
        {
            case "Tunnel.Add":
                await AddTunnelAsync(agent, args.ToArray());
                return true;

            case "Tunnel.Remove":
                await RemoveTunnelAsync(agent, args.FirstOrDefault() ?? "");
                return true;

            case "Tunnel.List":
            case "Status":
                return true;   // 重绘即刷新

            case "Quit":
            case "Exit":
                return false;

            case "Help":
                WriteResult("[bold]atc 调用：[/] atc.Tunnel.Add(\"名称\",\"类型\",\"本地端口\",\"公网端口|域名\") · atc.Tunnel.Remove(\"名称\") · atc.Tunnel.List() · atc.Status() · atc.Quit()");
                return true;

            default:
                WriteResult($"[red]未知调用：atc.{Markup.Escape(obj)}.{Markup.Escape(method)}(…)[/]（输入 atc.Help() 查看）");
                return true;
        }
    }

    private static async Task AddTunnelAsync(AgentHost agent, string[] args)
    {
        // 支持两种形式：
        //   atc 风格：("名称", "类型", "本地端口", "公网端口|域名") 或 ("类型", "本地端口", "公网端口|域名")
        //   旧命令：  add <类型>:<本地端口>:<公网端口|域名>
        if (args.Length == 0)
        {
            WriteResult("[red]用法：atc.Tunnel.Add(\"名称\",\"类型\",\"本地端口\",\"公网端口|域名\")[/]");
            return;
        }

        ProxyDefinition? def;
        if (args.Length == 1 && args[0].Contains(':'))
        {
            def = ParseTunnelSpec(args[0], agent.Proxies.Count);
        }
        else if (args.Length == 3)
        {
            def = BuildTunnelDef($"p{agent.Proxies.Count}", args[0], args[1], args[2]);
        }
        else if (args.Length == 4)
        {
            def = BuildTunnelDef(args[0], args[1], args[2], args[3]);
        }
        else
        {
            WriteResult("[red]参数数量不对：atc.Tunnel.Add(\"名称\",\"类型\",\"本地端口\",\"公网端口|域名\")[/]");
            return;
        }

        if (def is null)
            return;

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

        try { await agent.AddProxyAsync(def); }
        catch (Exception ex)
        {
            WriteResult($"[red]添加失败：{Markup.Escape(ex.Message)}[/]");
            return;
        }

        try
        {
            var (ok, msg) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            WriteResult(ok
                ? $"[green]✔ 隧道 [bold]{def.ProxyId}[/] 添加成功：{Markup.Escape(msg ?? "")}[/]"
                : $"[red]✘ 隧道 [bold]{def.ProxyId}[/] 注册失败：{Markup.Escape(msg ?? "未知错误")}[/]");
        }
        catch (TimeoutException)
        {
            WriteResult($"[yellow]隧道 [bold]{def.ProxyId}[/] 已加入待注册列表[/]（当前未连接，连接成功后自动注册）");
        }
    }

    private static async Task RemoveTunnelAsync(AgentHost agent, string proxyId)
    {
        if (string.IsNullOrWhiteSpace(proxyId))
        {
            WriteResult("[red]用法：remove <名称>[/]（如 remove p0）");
            return;
        }
        await agent.RemoveProxyAsync(proxyId);
        WriteResult($"[green]✔ 隧道 [bold]{Markup.Escape(proxyId)}[/] 已移除[/]");
    }

    /// <summary>命令结果输出（显示在日志区顶部，随下次刷新融入日志窗）</summary>
    private static void WriteResult(string markup)
    {
        // 在日志区上方插入一行结果（利用终端顶部空行）
        AnsiConsole.Write($"\x1b[{Format.TerminalHeight() - 1};1H\x1b[2K");
        AnsiConsole.MarkupLine($"  {markup}");
        AnsiConsole.Cursor.Move(CursorDirection.Up, 1);
        RenderInputLine("");
    }

    /// <summary>按分离参数构建隧道定义（atc.Tunnel.Add 风格）</summary>
    private static ProxyDefinition? BuildTunnelDef(string name, string type, string localPort, string remoteOrDomain)
    {
        if (!int.TryParse(localPort, out var lp))
        {
            WriteResult($"[red]无效的本地端口：{Markup.Escape(localPort)}[/]");
            return null;
        }
        if (type is "tcp" or "udp")
        {
            if (!int.TryParse(remoteOrDomain, out var rp))
            {
                WriteResult($"[red]无效的公网端口：{Markup.Escape(remoteOrDomain)}[/]");
                return null;
            }
            return new ProxyDefinition(name, type == "tcp" ? LinkType.Tcp : LinkType.Udp,
                "127.0.0.1", lp, RemotePort: rp);
        }
        if (type is "http" or "https")
        {
            return new ProxyDefinition(name, type == "http" ? LinkType.Http : LinkType.Https,
                "127.0.0.1", lp, Domain: remoteOrDomain);
        }
        WriteResult($"[red]未知隧道类型：{Markup.Escape(type)}[/]（支持 tcp/udp/http/https）");
        return null;
    }

    private static ProxyDefinition? ParseTunnelSpec(string spec, int index)
    {
        var parts = spec.Split(':');
        if (parts.Length < 3)
        {
            WriteResult($"[red]无效的隧道定义：{Markup.Escape(spec)}[/]");
            return null;
        }
        var type = parts[0];
        if (!int.TryParse(parts[1], out var localPort))
        {
            WriteResult($"[red]无效的本地端口：{Markup.Escape(parts[1])}[/]");
            return null;
        }
        var name = $"p{index}";
        if (type is "tcp" or "udp")
        {
            if (!int.TryParse(parts[2], out var remotePort))
            {
                WriteResult($"[red]无效的公网端口：{Markup.Escape(parts[2])}[/]");
                return null;
            }
            return new ProxyDefinition(name, type == "tcp" ? LinkType.Tcp : LinkType.Udp,
                "127.0.0.1", localPort, RemotePort: remotePort);
        }
        if (type is "http" or "https")
        {
            return new ProxyDefinition(name, type == "http" ? LinkType.Http : LinkType.Https,
                "127.0.0.1", localPort, Domain: parts[2]);
        }
        WriteResult($"[red]未知隧道类型：{Markup.Escape(type)}[/]（支持 tcp/udp/http/https）");
        return null;
    }

    /// <summary>Tab 补全：匹配命令前缀</summary>
    private static void TabComplete(StringBuilder input)
    {
        var text = input.ToString();
        if (text.Contains(' '))
            return; // 只补全命令名
        var match = Commands.FirstOrDefault(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            input.Clear();
            input.Append(match);
        }
    }
}
