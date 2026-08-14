using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Config;

/// <summary>
/// 服务端配置加载：server.toml → ServerConfig → ServerHostOptions。
/// </summary>
public static class ConfigLoader
{
    /// <summary>从 TOML 文件加载；文件不存在返回 null</summary>
    public static ServerConfig? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        return LoadString(File.ReadAllText(path));
    }

    /// <summary>从 toml 文本加载 ServerConfig（测试/内嵌用）</summary>
    public static ServerConfig? LoadString(string content)
    {
        var kv = MinimalToml.Parse(content);
        var cfg = new ServerConfig
        {
            BindPort = GetInt(kv, "bindPort", 7000),
            Token = GetString(kv, "token", ""),
            VhostHttpPort = GetInt(kv, "vhostHttpPort", 0),
            VhostHttpsPort = GetInt(kv, "vhostHttpsPort", 0),
            SubDomainHost = GetString(kv, "subDomainHost", ""),
            DashboardPort = GetInt(kv, "dashboardPort", 0),
            DashboardUser = GetString(kv, "dashboardUser", ""),
            DashboardPassword = GetString(kv, "dashboardPassword", ""),
            MaxPortsPerClient = GetInt(kv, "maxPortsPerClient", 0),
            WebBind = GetString(kv, "webBind", "127.0.0.1:7500"),
            ApiEnabled = GetBool(kv, "apiEnabled", false),
            WebToken = GetString(kv, "webToken", ""),
            WebTokenSalt = GetString(kv, "webTokenSalt", ""),
            Log =
            {
                File = GetString(kv, "log.file", ""),
                Level = GetString(kv, "log.level", "info"),
                MaxSizeMb = GetInt(kv, "log.maxSizeMb", 10),
            },
        };

        if (kv.TryGetValue("allowPorts", out var ap) && ap is List<object> ports && ports.Count > 0)
        {
            cfg.AllowPorts = new List<PortRange>();
            foreach (var item in ports)
            {
                if (item is int p && p >= 1 && p <= 65535)
                    cfg.AllowPorts.Add(new PortRange(p, p));
                else if (item is string s && TryParsePortRange(s, out var start, out var end))
                    cfg.AllowPorts.Add(new PortRange(start, end));
            }
        }
        else if (kv.TryGetValue("allowPorts", out var apOld) && apOld is List<int> oldPorts && oldPorts.Count > 0)
        {
            // 兼容旧格式：纯整数数组
            cfg.AllowPorts = oldPorts.Select(p => new PortRange(p, p)).ToList();
        }

        return cfg;
    }

    /// <summary>序列化 ServerConfig 并写入文件（token / webToken 写回用）</summary>
    public static void SaveServer(string path, ServerConfig cfg)
        => File.WriteAllText(path, MinimalToml.Write(cfg));

    /// <summary>ServerConfig → ServerHostOptions（用于 ServerHost 启动）</summary>
    public static ServerHostOptions ToHostOptions(ServerConfig cfg)
        => new(            cfg.BindPort,
            cfg.Token,
            VhostHttpPort: cfg.VhostHttpPort,
            VhostHttpsPort: cfg.VhostHttpsPort,
            SubDomainHost: cfg.SubDomainHost,
            DashboardPort: cfg.DashboardPort,
            AllowPorts: cfg.AllowPorts,
            DashboardUser: cfg.DashboardUser,
            DashboardPassword: cfg.DashboardPassword,
            MaxPortsPerClient: cfg.MaxPortsPerClient,
            WebBind: cfg.WebBind,
            ApiEnabled: cfg.ApiEnabled);

    /// <summary>加载 Agent 配置（agent.toml）；文件不存在返回 null</summary>
    public static AgentConfig? LoadAgentConfig(string path)
    {
        if (!File.Exists(path))
            return null;

        var kv = MinimalToml.Parse(File.ReadAllText(path));
        var cfg = new AgentConfig
        {
            ServerAddr = GetString(kv, "serverAddr", "127.0.0.1"),
            ServerPort = GetInt(kv, "serverPort", 7000),
            Token = GetString(kv, "token", ""),
            ClientId = GetString(kv, "clientId", ""),
            UseTls = GetBool(kv, "useTls", false),
            ReconnectIntervalSec = GetInt(kv, "reconnectInterval", 5),
            HealthIntervalSec = GetInt(kv, "healthInterval", 10),
            LogFile = GetString(kv, "log.file", ""),
            LogLevel = GetString(kv, "log.level", "info"),
        };

        // [[tunnels]] 表数组：tunnels.0.name / tunnels.1.name ...
        for (var i = 0; ; i++)
        {
            var prefix = $"tunnels.{i}.";
            if (!kv.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                break;

            cfg.Tunnels.Add(new AgentTunnelItem
            {
                Name = GetString(kv, prefix + "name", $"p{i}"),
                Type = GetString(kv, prefix + "type", "tcp"),
                LocalIp = GetString(kv, prefix + "localIp", "127.0.0.1"),
                LocalPort = GetInt(kv, prefix + "localPort", 0),
                RemotePort = GetInt(kv, prefix + "remotePort", 0),
                Domain = GetString(kv, prefix + "domain", ""),
                Subdomain = GetString(kv, prefix + "subdomain", ""),
                Group = GetString(kv, prefix + "group", ""),
            });
        }

        return cfg;
    }

    /// <summary>AgentConfig → AgentOptions（clientId 空时自动生成唯一标识）</summary>
    public static Client.AgentOptions ToAgentOptions(AgentConfig cfg)
    {
        var clientId = string.IsNullOrWhiteSpace(cfg.ClientId)
            ? GenerateClientId()
            : cfg.ClientId;

        return new Client.AgentOptions(
            cfg.ServerAddr,
            cfg.ServerPort,
            cfg.Token,
            clientId,
            UseTls: cfg.UseTls);
    }

    /// <summary>生成唯一客户端标识：agent-主机名-随机4位（同机多客户端不冲突）</summary>
    public static string GenerateClientId()
        => $"agent-{Environment.MachineName}-{Random.Shared.Next(0x10000):X4}";

    /// <summary>AgentConfig 隧道列表 → ProxyDefinition（type 字符串 → LinkType，含分组）</summary>
    public static List<Hosting.ProxyDefinition> ToProxyDefinitions(AgentConfig cfg)
    {
        var list = new List<Hosting.ProxyDefinition>();
        foreach (var p in cfg.Tunnels)
        {
            var linkType = p.Type.ToLowerInvariant() switch
            {
                "udp" => Protocol.LinkType.Udp,
                "http" => Protocol.LinkType.Http,
                "https" => Protocol.LinkType.Https,
                "tcp+udp" => Protocol.LinkType.Tcp | Protocol.LinkType.Udp,
                _ => Protocol.LinkType.Tcp,
            };
            list.Add(new Hosting.ProxyDefinition(p.Name, linkType, p.LocalIp, p.LocalPort,
                p.RemotePort > 0 ? p.RemotePort : null,
                string.IsNullOrEmpty(p.Domain) ? null : p.Domain,
                string.IsNullOrEmpty(p.Subdomain) ? null : p.Subdomain,
                string.IsNullOrEmpty(p.Group) ? null : p.Group));
        }
        return list;
    }

    /// <summary>解析端口区间字符串（"7071-7171" 或 "7071"）</summary>
    private static bool TryParsePortRange(string s, out int start, out int end)
    {
        start = end = 0;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        var dash = s.IndexOf('-');
        if (dash > 0)
        {
            if (int.TryParse(s[..dash], out start) && int.TryParse(s[(dash + 1)..], out end) &&
                start >= 1 && end <= 65535 && start <= end)
                return true;
        }
        else if (int.TryParse(s, out start) && start >= 1 && start <= 65535)
        {
            end = start;
            return true;
        }
        return false;
    }

    private static string GetString(Dictionary<string, object?> kv, string key, string def)        => kv.TryGetValue(key, out var v) && v is string s ? s : def;

    private static int GetInt(Dictionary<string, object?> kv, string key, int def)
        => kv.TryGetValue(key, out var v) && v is int i ? i : def;

    private static bool GetBool(Dictionary<string, object?> kv, string key, bool def)
        => kv.TryGetValue(key, out var v) && v is bool b ? b : def;
}
