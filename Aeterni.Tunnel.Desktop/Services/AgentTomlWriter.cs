using System.Text;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Desktop.Services;

/// <summary>
/// agent.toml 序列化（Desktop 内实现，输出与 Engine ConfigLoader.LoadAgentConfig 兼容）。
/// 保存连接信息 + 隧道全集，供下次启动直接读取。
/// </summary>
public static class AgentTomlWriter
{
    /// <summary>默认配置路径：可执行文件同目录 agent.toml（与 Web 的 server.toml 同惯例）</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "agent.toml");

    public static string Build(
        string serverAddr, int serverPort, string token, string clientId, bool useTls,
        IReadOnlyList<ProxyDefinition> tunnels, string theme = "dark")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"serverAddr = \"{Escape(serverAddr)}\"");
        sb.AppendLine($"serverPort = {serverPort}");
        if (theme.Length > 0)
            sb.AppendLine($"theme = \"{Escape(theme)}\"");
        if (!string.IsNullOrWhiteSpace(token))
            sb.AppendLine($"token = \"{Escape(token)}\"");
        if (!string.IsNullOrWhiteSpace(clientId))
            sb.AppendLine($"clientId = \"{Escape(clientId)}\"");
        if (useTls)
            sb.AppendLine("useTls = true");
        sb.AppendLine();

        foreach (var t in tunnels)
        {
            sb.AppendLine("[[tunnels]]");
            sb.AppendLine($"name = \"{Escape(t.ProxyId)}\"");
            sb.AppendLine($"type = \"{TypeString(t.LinkType)}\"");
            sb.AppendLine($"localIp = \"{Escape(t.LocalIp)}\"");
            sb.AppendLine($"localPort = {t.LocalPort}");
            if (t.RemotePort is > 0)
                sb.AppendLine($"remotePort = {t.RemotePort}");
            if (!string.IsNullOrWhiteSpace(t.Domain))
                sb.AppendLine($"domain = \"{Escape(t.Domain)}\"");
            if (!string.IsNullOrWhiteSpace(t.Subdomain))
                sb.AppendLine($"subdomain = \"{Escape(t.Subdomain)}\"");
            if (!string.IsNullOrWhiteSpace(t.Group))
                sb.AppendLine($"group = \"{Escape(t.Group)}\"");
            sb.AppendLine();
        }

        sb.AppendLine("[log]");
        sb.AppendLine("level = \"info\"");
        return sb.ToString();
    }

    /// <summary>LinkType → 配置字符串（与 ConfigLoader.ToProxyDefinitions 的解析兼容）</summary>
    private static string TypeString(LinkType type) => type switch
    {
        LinkType.Udp => "udp",
        LinkType.Http => "http",
        LinkType.Https => "https",
        var t when (t & LinkType.Udp) != 0 => "tcp+udp",
        _ => "tcp",
    };

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
