using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Aeterni.Tunnel.Engine.Hosting;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Spectre.Console;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// ATC 交互式界面（Terminal.Gui v2 完整重构）：
/// 左侧隧道列表（按 Group/类型分组，实时刷新） + 右侧日志窗口 + 底部指令输入（Roslyn REPL + 框架级补全）。
/// 安全模式：仅允许 atc.* 操作。
/// </summary>
public static class AgentTui
{
    // 安全模式：只放行 atc 表达式/调用，其余拒绝
    private static readonly Regex AtcOnlyPattern = new(
        @"^\s*(var\s+\w+\s*=\s*)?atc\.[\w().\[\],""'\s\-]+;?\s*$",
        RegexOptions.Compiled);

    /// <summary>列表行（IsGroup=true 为分组头）</summary>
    private sealed record RowItem(string Text, bool IsGroup);

    public static async Task RunAsync(AgentHost agent, CancellationToken ct)
    {
        if (Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("[agent] TUI 需要真实终端（当前输出被重定向）。请使用 --config 配置文件模式。");
            await agent.StartAsync(ct);
            await agent.StopAsync();
            return;
        }
        if (string.IsNullOrEmpty(typeof(AtcGlobals).Assembly.Location))
        {
            Console.Error.WriteLine("[agent] REPL 需要程序集可定位：单文件发布请启用 IncludeAllContentForSelfExtract=true");
            await agent.StopAsync();
            return;
        }

        var registered = new ConcurrentDictionary<string, (bool Ok, string? Msg)>();
        agent.ProxyRegistered += (id, ok, msg) => registered[id] = (ok, msg);

        var atc = new AtcContext(agent);
        var globals = new AtcGlobals(atc);
        ScriptState? scriptState = null;

        // ── Terminal.Gui 界面（诊断日志定位闪退） ──
        Application.Init();
        var win = new Window { Title = " AETERNI TUNNEL · Agent " };

        // ① 顶部 Banner（Apple 绿标题 + 灰副标 + 分隔线）
        var banner = new BannerView { Width = Dim.Fill(), Height = 3 };

        // ② Tab 标签头（隧道列表 / 日志 切换）
        var tabHeader = new TabHeaderView { Width = Dim.Fill(), Height = 1 };

        // ③ 内容区（单面板：隧道列表 / 日志 叠放，Tab 切换 Visible）
        var content = new View { Width = Dim.Fill(), Height = Dim.Fill(4) };
        var tunnelList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        var tunnelFrame = new FrameView
        {
            Title = " 隧道列表 ",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Rounded,
        };
        tunnelFrame.Add(tunnelList);
        var logView = new LogView { Width = Dim.Fill(), Height = Dim.Fill() };
        var logFrame = new FrameView
        {
            Title = " 日志 ",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Rounded,
        };
        logFrame.Add(logView);
        content.Add(tunnelFrame, logFrame);

        // ④ 指令输入区块
        var input = new TextField { X = 1, Y = 0, Width = Dim.Fill(1) };
        var inputFrame = new FrameView
        {
            Title = " 指令 ",
            Width = Dim.Fill(),
            Height = 3,
            BorderStyle = LineStyle.Rounded,
        };
        inputFrame.Add(input);

        // ⑤ 底部状态栏（连接 / 隧道数 / 流量 / 键位提示）
        var statusBar = new StatusBarView { Width = Dim.Fill(), Height = 1 };

        // 布局坐标（纵向链）
        banner.Y = 0;
        tabHeader.Y = Pos.Bottom(banner);
        content.Y = Pos.Bottom(tabHeader);
        inputFrame.Y = Pos.Bottom(content);
        statusBar.Y = Pos.Bottom(inputFrame);
        win.Add(banner, tabHeader, content, inputFrame, statusBar);

        // Tab 切换面板（0=隧道列表 1=日志）
        void SwitchTab(int n)
        {
            tunnelFrame.Visible = n == 0;
            logFrame.Visible = n == 1;
            tabHeader.Active = n;
        }
        SwitchTab(0);

        // ── 自绘补全弹窗（浮动在输入框上方，完全可控） ──
        var popup = new Window { BorderStyle = LineStyle.Rounded, Visible = false };
        var popupLabels = new List<Label>();
        var popupSuggestions = new List<Suggestion>();
        var popupSelected = 0;
        win.Add(popup);   // 最后 Add → z-order 最上层

        void UpdatePopup()
        {
            var suggestions = GetSuggestions(input.Text ?? "");
            if (suggestions.Count == 0)
            {
                popup.Visible = false;
                return;
            }
            foreach (var l in popupLabels)
                popup.Remove(l);
            popupLabels.Clear();

            var shown = suggestions.Take(8).ToList();
            popupSelected = Math.Clamp(popupSelected, 0, shown.Count - 1);
            for (var i = 0; i < shown.Count; i++)
            {
                var isSel = i == popupSelected;
                var label = new Label
                {
                    X = 1,
                    Y = i,
                    Text = (isSel ? "▶ " : "  ") + shown[i].Title,
                };
                popup.Add(label);
                popupLabels.Add(label);
            }
            popupSuggestions = shown;
            popup.X = Pos.Left(inputFrame) + 1;
            popup.Y = Pos.Bottom(content) - shown.Count - 1;   // 输入框上方
            popup.Width = 58;
            popup.Height = shown.Count + 2;
            popup.Visible = true;
        }
        // 提前 EndInit：FrameView 的 Title 会在 EndInit 时动态添加子视图，
        // 若等到 Application.Run 的 EndInit 阶段（正在枚举 Subviews）才加 → Collection modified 崩溃。
        // 手动先初始化，Run 时因 IsInitialized 跳过。
        win.EndInit();
        // 初始焦点给指令输入框（主循环启动后设置才生效）
        Application.Invoke(() => input.SetFocus());


        // 隧道列表数据源 + 分组头高亮（SetSource 由首次 RefreshTunnels 懒设置——所有触发点都在 Run 之后，避免 EndInit 枚举冲突）
        var rows = new ObservableCollection<object>();
        tunnelList.RowRender += (_, e) =>
        {
            if (e.Row >= 0 && e.Row < rows.Count && rows[e.Row] is RowItem { IsGroup: true })
                e.RowAttribute = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.BrightCyan, Terminal.Gui.Drawing.Color.Black);
        };

        // 日志 → 右侧窗口（自动滚动）
        agent.LogLine += s => Application.Invoke(() =>
            AppendLog(logView, $"[{DateTime.Now:HH:mm:ss}] {s}"));

        // 隧道变化 / 每秒 → 刷新左侧列表 + 状态栏（流量、状态实时）
        agent.ProxyRegistered += (_, _, _) => Application.Invoke(() => RefreshTunnels(agent, rows, registered, tunnelList));
        Application.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            Application.Invoke(() =>
            {
                RefreshTunnels(agent, rows, registered, tunnelList);
                var (up, down) = agent.GetTraffic().Values.Aggregate((0L, 0L), (acc, t) => (acc.Item1 + t.Up, acc.Item2 + t.Down));
                statusBar.SetStatus(agent.IsConnected, agent.Proxies.Count, up, down);
            });
            return true;
        });

        // 后台连接：主循环启动后 100ms 再连（避免 UI 初始化期间事件修改集合 → EndInit 枚举冲突）
        Application.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            _ = Task.Run(async () =>
            {
                try { await agent.StartAsync(ct); }
                catch (Exception ex) { Console.Error.WriteLine($"[agent] 启动异常：{ex.Message}"); }
            });
            return false;   // 仅执行一次
        });

        // 回车 → 执行（安全模式）
        // 输入变化 → 更新补全弹窗
        input.TextChanged += (_, _) => UpdatePopup();

        input.KeyDown += async (_, key) =>
        {
            // 补全弹窗交互（↑↓ 选择 / Tab 接受 / Esc 关闭）
            if (popup.Visible && popupSuggestions.Count > 0)
            {
                if (key == Key.CursorUp)
                {
                    popupSelected = Math.Max(0, popupSelected - 1);
                    UpdatePopup();
                    key.Handled = true;
                    return;
                }
                if (key == Key.CursorDown)
                {
                    popupSelected = Math.Min(popupSuggestions.Count - 1, popupSelected + 1);
                    UpdatePopup();
                    key.Handled = true;
                    return;
                }
                if (key == Key.Tab)
                {
                    input.Text += popupSuggestions[popupSelected].Replacement;
                    popup.Visible = false;
                    key.Handled = true;
                    return;
                }
                if (key == Key.Esc)
                {
                    popup.Visible = false;
                    key.Handled = true;
                    return;
                }
            }
            // 无补全弹窗时：Tab 切换面板（隧道列表 / 日志）
            if (key == Key.Tab)
            {
                var next = tunnelFrame.Visible ? 1 : 0;
                SwitchTab(next);
                key.Handled = true;
                return;
            }

            if (key == Key.Enter)
            {
                popup.Visible = false;
                var code = input.Text ?? "";
                if (code.Trim().Length > 0)
                {
                    input.Text = "";
                    scriptState = await ExecuteAsync(globals, logView, code, atc, scriptState);
                    RefreshTunnels(agent, rows, registered, tunnelList);
                }
                key.Handled = true;
            }
        };

        try
        {
            Application.Run(win);
        }
        catch (Exception ex)
        {
        }
        Application.Shutdown();
        await agent.StopAsync();
    }

    // ═════════ 补全候选（静态白名单：仅 atc 接口，杜绝 C# 内置提示） ═════════

    private static readonly IReadOnlyList<Suggestion> AtcSuggestions =
    [
        new Suggestion(0, "Tunnel", "P Tunnel → 隧道管理"),
        new Suggestion(0, "TunnelCount", "P TunnelCount → 当前隧道数"),
        new Suggestion(0, "Status", "P Status → 连接状态"),
        new Suggestion(0, "Connected", "P Connected → 是否已连接"),
        new Suggestion(0, "Version", "P Version → 版本号"),
        new Suggestion(0, "Quit", "M Quit() → 退出"),
        new Suggestion(0, "Help", "M Help() → 用法说明"),
    ];

    private static readonly IReadOnlyList<Suggestion> TunnelSuggestions =
    [
        new Suggestion(0, "Add(", "M Add(名称, 类型, 本地端口, 公网端口|域名)"),
        new Suggestion(0, "Remove(", "M Remove(隧道名称)"),
        new Suggestion(0, "List", "M List() → 隧道名列表"),
        new Suggestion(0, "Count", "P Count → 隧道数"),
    ];

    private static IReadOnlyList<Suggestion> GetSuggestions(string text)
    {
        if (!text.StartsWith("atc", StringComparison.Ordinal))
            return [];

        // 当前输入的最后一段（. / ( / 空格 之后）作为过滤前缀
        var lastWord = text[(text.LastIndexOfAny(['.', '(', ' ']) + 1)..];
        var all = text.Contains("atc.Tunnel.", StringComparison.Ordinal)
            ? TunnelSuggestions
            : AtcSuggestions;
        if (lastWord.Length == 0)
            return all;
        return all.Where(s => s.Replacement.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ═════════ 隧道列表（分组 + 实时刷新） ═════════

    private static bool _sourceSet;

    private static void RefreshTunnels(
        AgentHost agent, ObservableCollection<object> rows,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered,
        ListView tunnelList)
    {
        // 首次调用时设置数据源（所有触发点都在 Run 之后，安全）
        if (!_sourceSet)
        {
            tunnelList.SetSource(rows);
            _sourceSet = true;
        }

        var proxies = agent.Proxies
            .OrderBy(p => p.Group ?? p.LinkType.ToString())
            .ThenBy(p => p.ProxyId)
            .ToList();

        var newRows = new List<object>();
        string? currentGroup = null;
        foreach (var p in proxies)
        {
            var group = p.Group ?? p.LinkType.ToString().ToUpperInvariant();
            if (group != currentGroup)
            {
                newRows.Add(new RowItem($"══ {group} ══", true));
                currentGroup = group;
            }

            var remote = p.RemotePort is not null ? $"0.0.0.0:{p.RemotePort}" : p.Domain ?? "-";
            var (up, down) = agent.GetTraffic().TryGetValue(p.ProxyId, out var t) ? t : (0L, 0L);
            var ok = !agent.IsConnected || (registered.TryGetValue(p.ProxyId, out var r) && r.Ok);
            var icon = !agent.IsConnected ? "○" : ok ? "●" : "○";
            newRows.Add(new RowItem(
                $"{icon} {p.ProxyId}  {p.LinkType.ToString().ToLowerInvariant()}  {remote}  ↑{Format.Bytes(up)} ↓{Format.Bytes(down)}",
                false));
        }
        if (newRows.Count == 0)
            newRows.Add(new RowItem("（暂无隧道 —— 输入 atc.Tunnel.Add(\"mc\",\"tcp\",\"25565\",\"6071\") 添加）", true));

        rows.Clear();
        foreach (var r in newRows)
            rows.Add(r);
    }

    // ═════════ 执行（安全模式：仅 atc） ═════════

    private static async Task<ScriptState?> ExecuteAsync(
        AtcGlobals globals, LogView logView, string code, AtcContext atc, ScriptState? state)
    {
        var trimmed = code.Trim();
        AppendLog(logView, $"> {trimmed}");

        switch (trimmed.ToLowerInvariant())
        {
            case "help":
                AppendLog(logView, atc.Help());
                return state;
            case "quit":
            case "exit":
                Application.Invoke(() => Application.RequestStop());
                return state;
            case "clear":
                Application.Invoke(() => logView.Text = "");
                return state;
        }

        if (!AtcOnlyPattern.IsMatch(trimmed))
        {
            AppendLog(logView, "⚠ 仅允许 atc 操作（安全模式）：atc.Tunnel.Add / atc.TunnelCount / atc.Status / atc.Quit…");
            return state;
        }

        try
        {
            state = state is null
                ? await CSharpScript.RunAsync(trimmed, ScriptOptionsOf(globals), globals: globals, globalsType: typeof(AtcGlobals))
                : await state.ContinueWithAsync(trimmed);
            if (globals.atc.ExitRequested)
            {
                Application.Invoke(() => Application.RequestStop());
                return state;
            }
            var value = await UnwrapAsync(state.ReturnValue);
            if (value is not null)
                AppendLog(logView, value.ToString() ?? "");
        }
        catch (CompilationErrorException ex)
        {
            AppendLog(logView, $"✗ {string.Join(" / ", ex.Diagnostics.Take(2).Select(d => d.ToString()))}");
        }
        catch (Exception ex)
        {
            AppendLog(logView, $"✗ {ex.GetType().Name}：{ex.Message}");
        }
        return state;
    }

    private static async Task<object?> UnwrapAsync(object? value)
    {
        while (value is Task task)
        {
            await task;
            if (!task.GetType().IsGenericType)
                return "(completed)";
            value = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        return value;
    }

    private static ScriptOptions ScriptOptionsOf(AtcGlobals globals)
        => ScriptOptions.Default
            .AddReferences(typeof(AtcGlobals).Assembly)
            .AddImports("System", "System.Linq", "System.Collections.Generic", "Aeterni.Tunnel.Engine.Hosting");

    // ═════════ 输出与补全 ═════════

    private static void AppendLog(LogView logView, string text)
    {
        logView.Add(text);
    }

}
