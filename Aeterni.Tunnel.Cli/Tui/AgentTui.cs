using System.Collections.Concurrent;
using System.Text;
using Aeterni.Tunnel.Engine.Hosting;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Spectre.Console;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// ATC 全屏交互式 REPL（VS C# Interactive 风格）：
/// 上部隧道状态表（Spectre Table 规整）+ 中部日志 + 底部命令输入。
/// 输入走 Roslyn 脚本引擎：atc.Tunnel.Add("mc","tcp","25565","6071") 或任意 C# 表达式（变量跨输入保持）。
/// 实时刷新（后台 1s 覆盖重绘，无频闪），Tab 补全快捷命令，Ctrl+C 退出。
/// </summary>
public static class AgentTui
{
    private static readonly string[] QuickCommands = ["help", "quit", "exit", "clear"];

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

        // ── Roslyn REPL 状态 ──
        var scriptOptions = ScriptOptions.Default
            .AddReferences(typeof(AtcContext).Assembly)
            .AddImports("System", "System.Linq", "System.Collections.Generic", "Aeterni.Tunnel.Engine.Hosting");
        var atc = new AtcContext(agent);
        ScriptState? scriptState = null;

        try { AnsiConsole.Cursor.Hide(); } catch (IOException) { }

        var input = new StringBuilder();
        var result = "";
        var running = true;
        var lastRender = 0L;

        try
        {
            while (running && !ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            (result, running, scriptState) = await EvalAsync(atc, scriptState, input.ToString(), running);
                            input.Clear();
                            if (running)
                                RenderAll(agent, registered, logs, input.ToString(), result);
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
                        RenderAll(agent, registered, logs, input.ToString(), result);
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

    /// <summary>执行输入：快捷命令或 Roslyn C# 表达式；返回 (结果文本, 是否继续, 新脚本状态)</summary>
    private static async Task<(string Result, bool Running, ScriptState? State)> EvalAsync(
        AtcContext atc, ScriptState? state, string code, bool running)
    {
        var trimmed = code.Trim();
        if (trimmed.Length == 0)
            return ("", running, state);

        switch (trimmed.ToLowerInvariant())
        {
            case "help":
                return (atc.Help(), running, state);
            case "quit":
            case "exit":
                return ("bye", false, state);
            case "clear":
                return ("", running, state);
        }

        try
        {
            state = state is null
                ? await CSharpScript.RunAsync(trimmed, ScriptOptionsOf(atc), globals: atc, globalsType: typeof(AtcContext))
                : await state.ContinueWithAsync(trimmed);
            if (atc.ExitRequested)
                return (state.ReturnValue?.ToString() ?? "", false, state);
            var value = state.ReturnValue;
            return (value is null ? "" : value.ToString() ?? "", running, state);
        }
        catch (CompilationErrorException ex)
        {
            var errors = string.Join(" / ", ex.Diagnostics.Take(3).Select(d => d.ToString()));
            return ($"[red]编译错误：{Markup.Escape(errors)}[/]", running, state);
        }
        catch (Exception ex)
        {
            return ($"[red]{Markup.Escape(ex.GetType().Name)}：{Markup.Escape(ex.Message)}[/]", running, state);
        }
    }

    private static ScriptOptions ScriptOptionsOf(AtcContext atc)
        => ScriptOptions.Default
            .AddReferences(typeof(AtcContext).Assembly)
            .AddImports("System", "System.Linq", "System.Collections.Generic", "Aeterni.Tunnel.Engine.Hosting");

    // ═════════ 渲染（全屏覆盖，无频闪） ═════════

    private static void RenderAll(
        AgentHost agent,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered,
        ConcurrentQueue<string> logs,
        string input, string result)
    {
        var height = Format.TerminalHeight();
        var width = Format.TerminalWidth();

        AnsiConsole.Write("\x1b[H");   // 光标归位，覆盖重绘（不清屏 → 不闪）

        // ① 品牌 + 状态行
        var state = agent.IsConnected ? "[green]● 已连接[/]" : "[yellow]○ 重连中[/]";
        AnsiConsole.MarkupLine($"  [bold green]AETERNI[/] [dim]TUNNEL · Agent[/]  {state}  隧道 [bold]{agent.Proxies.Count}[/] 条");

        // ② 隧道状态表（Spectre Table 规整；按高度截断）
        var tableHeight = Math.Min(agent.Proxies.Count + 4, Math.Max(5, height - 9));
        RenderTable(agent, registered, tableHeight);

        // ③ 结果行（命令输出）
        if (result.Length > 0)
        {
            var clipped = result.Length > width - 4 ? result[..(width - 4)] : result;
            AnsiConsole.MarkupLine($"  {clipped}");
        }

        // ④ 日志窗（填满剩余）
        var logRows = Math.Max(1, height - tableHeight - (result.Length > 0 ? 8 : 7));
        AnsiConsole.MarkupLine($"  [dim]{new string('─', Math.Max(20, width - 4))}[/]");
        var logLines = logs.TakeLast(logRows).ToArray();
        if (logLines.Length == 0)
            logLines = ["[dim]（等待日志…）[/]"];
        foreach (var line in logLines)
        {
            var clipped = line.Length > width - 4 ? line[..(width - 4)] : line;
            AnsiConsole.MarkupLine($"  {Markup.Escape(clipped)}");
        }

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
            AnsiConsole.MarkupLine("  [dim]（暂无隧道——输入 [cyan]atc.Tunnel.Add(\"mc\",\"tcp\",\"25565\",\"6071\")[/] 添加）[/]");
            return;
        }

        var table = new Table { Border = TableBorder.Rounded, Expand = true, Width = Math.Max(30, Format.TerminalWidth() - 4) };
        table.AddColumn(new TableColumn("名称").Centered());
        table.AddColumn(new TableColumn("类型").Centered());
        table.AddColumn(new TableColumn("远程地址"));
        table.AddColumn(new TableColumn("↑ 累计").Centered());
        table.AddColumn(new TableColumn("↓ 累计").Centered());
        table.AddColumn(new TableColumn("状态").Centered());

        foreach (var p in rows)
        {
            (bool Ok, string? Msg) reg = registered.TryGetValue(p.ProxyId, out var r) ? r : (false, null);
            (long Up, long Down) traffic = agent.GetTraffic().TryGetValue(p.ProxyId, out var t) ? t : (0L, 0L);
            var remote = p.RemotePort is not null ? $"0.0.0.0:{p.RemotePort}" : p.Domain ?? "-";
            var status = !agent.IsConnected
                ? "[grey]未连接[/]"
                : reg.Ok ? "[green]● 在线[/]"
                : reg.Msg is not null ? "[red]● 失败[/]"
                : "[grey]○ 注册中[/]";
            table.AddRow(Markup.Escape(p.ProxyId), p.LinkType.ToString().ToLowerInvariant(),
                Markup.Escape(remote), $"↑{Format.Bytes(traffic.Up)}", $"↓{Format.Bytes(traffic.Down)}", status);
        }
        if (rows.Count < agent.Proxies.Count)
            table.Caption = new TableTitle($"… 还有 {agent.Proxies.Count - rows.Count} 条未显示");
        AnsiConsole.Write(table);
    }

    private static void RenderInputLine(string input)
    {
        var row = Format.TerminalHeight();
        AnsiConsole.Write($"\x1b[{row};1H\x1b[2K");
        var display = input.Length > 80 ? input[^80..] : input;
        AnsiConsole.Markup($"[bold green]ats›[/] {Markup.Escape(display)}");
    }

    /// <summary>Tab 补全快捷命令</summary>
    private static void TabComplete(StringBuilder input)
    {
        var text = input.ToString();
        if (text.Contains(' '))
            return;
        var match = QuickCommands.FirstOrDefault(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            input.Clear();
            input.Append(match);
        }
    }
}
