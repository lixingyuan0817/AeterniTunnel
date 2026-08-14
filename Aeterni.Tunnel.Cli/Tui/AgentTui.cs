using System.Collections.Concurrent;
using Aeterni.Tunnel.Engine.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// ATC 客户端 TUI：结合新 Engine（AgentHost）实时状态展示。
/// 布局：品牌状态行 + 隧道状态表（名称/类型/远程/双向流量/速率/在线）+ 实时日志窗。
/// 整页无闪渲染（RenderLoop），随终端缩放自适应，Ctrl+C 退出。
/// </summary>
public static class AgentTui
{
    private const int LogKeepLines = 12;

    /// <summary>进入 TUI（内部 StartAsync → 渲染循环 → Ctrl+C 后 StopAsync）</summary>
    public static async Task RunAsync(AgentHost agent, CancellationToken ct)
    {
        // TUI 需要真实终端：输出被重定向（管道/文件）时无法渲染，明确提示后退出
        if (Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("[agent] TUI 需要真实终端（当前输出被重定向）。请直接运行，或去掉 --tui 使用日志模式。");
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

        await agent.StartAsync(ct);

        var sampler = new TrafficRateSampler();
        await RenderLoop.RunAsync(() => BuildFrame(agent, registered, logs, sampler), ct);

        await agent.StopAsync();
    }

    /// <summary>组装一帧：品牌状态行 + 隧道表 + 日志窗（垂直堆叠，全宽）</summary>
    private static IRenderable BuildFrame(
        AgentHost agent,
        ConcurrentDictionary<string, (bool Ok, string? Msg)> registered,
        ConcurrentQueue<string> logs,
        TrafficRateSampler sampler)
    {
        // ① 品牌 + 连接状态行
        var state = agent.IsConnected
            ? "[green]● 已连接[/]"
            : "[yellow]○ 重连中[/]";
        var header = new Markup(
            $"[bold italic]AETERNI[/] [dim]TUNNEL · Agent[/]   {state}   [dim]隧道 {agent.Proxies.Count} 条[/]   [dim]{agent.TargetDescription()}[/]");

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
        table.AddColumn(new TableColumn("实时速率").Centered());
        table.AddColumn(new TableColumn("状态").Centered());

        foreach (var p in agent.Proxies)
        {
            (bool Ok, string? Msg) reg = registered.TryGetValue(p.ProxyId, out var r) ? r : (false, null);
            (long Up, long Down) traffic = agent.GetTraffic().TryGetValue(p.ProxyId, out var t) ? t : (0L, 0L);
            var rate = sampler.Sample(p.ProxyId, traffic.Up, traffic.Down);

            var remote = p.RemotePort is not null ? $"0.0.0.0:{p.RemotePort}" : p.Domain ?? "-";
            // 状态：未连接=等待重连（灰）；成功=在线；有失败结果=失败（红）；已连接未注册=注册中
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
                $"↑{Format.Rate(rate.Up)} ↓{Format.Rate(rate.Down)}",
                status);
        }

        // ③ 实时日志窗
        var logPanel = new Panel(string.Join("\n", logs.Select(Markup.Escape)))
        {
            Header = new PanelHeader(" 日志 "),
            Border = BoxBorder.Rounded,
        };

        // 垂直堆叠组装
        var grid = new Grid().AddColumn(new GridColumn().PadRight(0));
        grid.AddRow(new Markup(" "));
        grid.AddRow(header);
        grid.AddRow(new Markup(" "));
        grid.AddRow(table);
        grid.AddRow(logPanel);
        return grid;
    }

    /// <summary>目标服务端描述（AgentHost 内部连接目标的辅助展示）</summary>
    private static string TargetDescription(this AgentHost agent)
    {
        try
        {
            // 通过连接状态 + 日志已有信息，展示简洁标识
            return agent.IsConnected ? "已注册隧道" : "等待连接";
        }
        catch
        {
            return "";
        }
    }
}
