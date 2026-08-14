using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// C# REPL 全局上下文对象：脚本里可直接操作 `atc` 对象（VS C# Interactive 风格）。
/// 例：atc.Tunnel.Add("mc", "tcp", "25565", "6071") / atc.TunnelCount / atc.Status / atc.Quit()
/// </summary>
public sealed class AtcContext
{
    private readonly AgentHost _agent;

    /// <summary>隧道管理子上下文</summary>
    public AtcTunnelContext Tunnel { get; }

    /// <summary>当前隧道数</summary>
    public int TunnelCount => _agent.Proxies.Count;

    /// <summary>是否已连接</summary>
    public bool Connected => _agent.IsConnected;

    /// <summary>连接状态文本</summary>
    public string Status => _agent.IsConnected ? "connected" : "reconnecting";

    /// <summary>版本</summary>
    public string Version => "10.2.0";

    /// <summary>退出标志（Quit() 调用后 REPL 结束）</summary>
    public bool ExitRequested { get; private set; }

    public AtcContext(AgentHost agent)
    {
        _agent = agent;
        Tunnel = new AtcTunnelContext(agent);
    }

    /// <summary>退出 REPL</summary>
    public string Quit()
    {
        ExitRequested = true;
        return "bye";
    }

    /// <summary>帮助</summary>
    public string Help() =>
        """
        atc 对象调用：
          atc.Tunnel.Add("名称", "类型", "本地端口", "公网端口|域名")  添加隧道（tcp/udp/http/https）
          atc.Tunnel.Remove("名称")     移除隧道
          atc.Tunnel.List()             列出隧道
          atc.TunnelCount / atc.Status / atc.Connected / atc.Version
          atc.Quit()                    退出
        也可输入任意 C# 表达式（支持变量，如 var x = 1; x * 2）
        """;
}

/// <summary>隧道管理子上下文（atc.Tunnel.*）</summary>
public sealed class AtcTunnelContext
{
    private readonly AgentHost _agent;

    public AtcTunnelContext(AgentHost agent) => _agent = agent;

    /// <summary>当前隧道数</summary>
    public int Count => _agent.Proxies.Count;

    /// <summary>列出隧道名</summary>
    public IReadOnlyList<string> List() => _agent.Proxies.Select(p => p.ProxyId).ToList();

    /// <summary>添加隧道；返回结果描述（含注册结果/错误）</summary>
    public async Task<string> Add(string name, string type, string localPort, string remoteOrDomain)
    {
        var def = BuildTunnelDef(name, type, localPort, remoteOrDomain);
        if (def is null)
            return "invalid arguments: 类型支持 tcp/udp/http/https";

        // 等待该隧道注册结果（事件 + 10s 超时）
        var tcs = new TaskCompletionSource<(bool Ok, string? Msg)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string, bool, string?> handler = null!;
        handler = (id, ok, msg) =>
        {
            if (id == def.ProxyId)
            {
                tcs.TrySetResult((ok, msg));
                _agent.ProxyRegistered -= handler;
            }
        };
        _agent.ProxyRegistered += handler;

        try { await _agent.AddProxyAsync(def); }
        catch (Exception ex) { return $"add failed: {ex.Message}"; }

        try
        {
            var (ok, msg) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return ok ? $"tunnel '{def.ProxyId}' added: {msg}" : $"tunnel '{def.ProxyId}' register failed: {msg}";
        }
        catch (TimeoutException)
        {
            return $"tunnel '{def.ProxyId}' queued (not connected yet, auto-register on connect)";
        }
    }

    /// <summary>移除隧道</summary>
    public async Task<string> Remove(string name)
    {
        await _agent.RemoveProxyAsync(name);
        return $"tunnel '{name}' removed";
    }

    private static ProxyDefinition? BuildTunnelDef(string name, string type, string localPort, string remoteOrDomain)
    {
        if (!int.TryParse(localPort, out var lp))
            return null;
        if (type is "tcp" or "udp")
        {
            if (!int.TryParse(remoteOrDomain, out var rp))
                return null;
            return new ProxyDefinition(name, type == "tcp" ? LinkType.Tcp : LinkType.Udp,
                "127.0.0.1", lp, RemotePort: rp);
        }
        if (type is "http" or "https")
        {
            return new ProxyDefinition(name, type == "http" ? LinkType.Http : LinkType.Https,
                "127.0.0.1", lp, Domain: remoteOrDomain);
        }
        return null;
    }
}
