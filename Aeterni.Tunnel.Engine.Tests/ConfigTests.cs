using Aeterni.Tunnel.Common;
using Aeterni.Tunnel.Engine.Config;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

public class MinimalTomlTests
{
    [Fact]
    public void Parse_BasicValues()
    {
        var kv = MinimalToml.Parse("""
            # 注释
            bindPort = 7070
            token = "my-token"
            vhostHttpPort = 8080
            dashboardPort = 0
            [log]
            file = "aeterni.log"
            level = "info"
            maxSizeMb = 10
            """);

        Assert.Equal(7070, kv["bindPort"]);
        Assert.Equal("my-token", kv["token"]);
        Assert.Equal(8080, kv["vhostHttpPort"]);
        Assert.Equal(0, kv["dashboardPort"]);
        Assert.Equal("aeterni.log", kv["log.file"]);
        Assert.Equal("info", kv["log.level"]);
        Assert.Equal(10, kv["log.maxSizeMb"]);
    }

    [Fact]
    public void Parse_IntArray()
    {
        var kv = MinimalToml.Parse("allowPorts = [50000, 50100, 50200]");
        // 数组项整数存 int（容器为 List<object>，兼容区间字符串项）
        var ports = Assert.IsType<List<object>>(kv["allowPorts"]);
        Assert.Equal(new object[] { 50000, 50100, 50200 }, ports);
    }

    [Fact]
    public void Parse_AllowPortsWithRange_RoundTrips()
    {
        var kv = MinimalToml.Parse("allowPorts = [7071, \"7071-7171\", 17061]");
        var ports = Assert.IsType<List<object>>(kv["allowPorts"]);
        Assert.Equal(new object[] { 7071, "7071-7171", 17061 }, ports);

        // 写回 → 重新解析 → ConfigLoader 还原区间
        var cfg = ConfigLoader.LoadString("bindPort = 17070\nallowPorts = [7071, \"7071-7171\", 17061]")!;
        Assert.Equal(3, cfg.AllowPorts!.Count);
        Assert.Equal(new PortRange(7071, 7071), cfg.AllowPorts[0]);
        Assert.Equal(new PortRange(7071, 7171), cfg.AllowPorts[1]);
        Assert.Equal(new PortRange(17061, 17061), cfg.AllowPorts[2]);

        var text = ConfigLoader.Write(cfg);
        var cfg2 = ConfigLoader.LoadString(text)!;
        Assert.Equal(3, cfg2.AllowPorts!.Count);
        Assert.Equal(new PortRange(7071, 7171), cfg2.AllowPorts[1]);
    }

    [Fact]
    public void Parse_KeyCaseInsensitive()
    {
        var kv = MinimalToml.Parse("BindPort = 7000");
        Assert.Equal(7000, kv["bindport"]);
    }
}

public class ConfigLoaderTests
{
    [Fact]
    public void Load_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "at-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "server.toml");
            File.WriteAllText(path, """
                bindPort = 7070
                token = "secret"
                vhostHttpPort = 8080
                subDomainHost = "example.com"
                dashboardPort = 7500
                allowPorts = [50000, 50100]
                [log]
                file = "server.log"
                level = "warn"
                """);

            var cfg = ConfigLoader.Load(path);
            Assert.NotNull(cfg);
            Assert.Equal(7070, cfg!.BindPort);
            Assert.Equal("secret", cfg.Token);
            Assert.Equal(8080, cfg.VhostHttpPort);
            Assert.Equal("example.com", cfg.SubDomainHost);
            Assert.Equal(7500, cfg.DashboardPort);
            Assert.Equal(2, cfg.AllowPorts!.Count);
            Assert.Equal("server.log", cfg.Log.File);
            Assert.Equal("warn", cfg.Log.Level);

            var hostOpts = ConfigLoader.ToHostOptions(cfg);
            Assert.Equal(7070, hostOpts.BindPort);
            Assert.Equal("secret", hostOpts.Token);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(ConfigLoader.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".toml")));
    }

    [Fact]
    public void Parse_WebBind_ApiEnabled_WebToken()
    {
        var dir = Directory.CreateTempSubdirectory("ats-cfg-test");
        try
        {
            var path = Path.Combine(dir.FullName, "server.toml");
            File.WriteAllText(path, """
                bindPort = 9001
                token = "t"
                webBind = "0.0.0.0:8080"
                apiEnabled = true
                webToken = "ABCD1234"
                """);

            var cfg = ConfigLoader.Load(path)!;
            var options = ConfigLoader.ToHostOptions(cfg);

            Assert.Equal("0.0.0.0:8080", cfg.WebBind);
            Assert.Equal("0.0.0.0:8080", options.WebBind);
            Assert.True(cfg.ApiEnabled);
            Assert.True(options.ApiEnabled);
            Assert.Equal("ABCD1234", cfg.WebToken);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void LoadAgentConfig_ParsesProxiesTableArray()
    {
        var dir = Directory.CreateTempSubdirectory("atc-cfg-test");
        try
        {
            var path = Path.Combine(dir.FullName, "agent.toml");
            File.WriteAllText(path, """
                serverAddr = "1.2.3.4"
                serverPort = 7070
                token = "secret"
                clientId = "mc-server"
                useTls = true
                healthInterval = 5

                [[tunnels]]
                name = "mc"
                type = "tcp"
                localIp = "127.0.0.1"
                localPort = 25565
                remotePort = 25566

                [[tunnels]]
                name = "web"
                type = "http"
                localIp = "127.0.0.1"
                localPort = 8080
                domain = "web.example.com"
                group = "web-group"

                [[tunnels]]
                name = "mc-udp"
                type = "udp"
                localPort = 19132
                remotePort = 19133
                """);

            var cfg = ConfigLoader.LoadAgentConfig(path)!;
            var opts = ConfigLoader.ToAgentOptions(cfg);
            var proxies = ConfigLoader.ToProxyDefinitions(cfg);

            Assert.Equal("1.2.3.4", cfg.ServerAddr);
            Assert.Equal(7070, cfg.ServerPort);
            Assert.Equal("mc-server", cfg.ClientId);
            Assert.True(cfg.UseTls);
            Assert.Equal(5, cfg.HealthIntervalSec);
            Assert.Equal("secret", opts.Token);
            Assert.True(opts.UseTls);
            Assert.Equal("mc-server", opts.ClientId); // 显式 clientId 原样使用

            Assert.Equal(3, proxies.Count);
            Assert.Equal("mc", proxies[0].ProxyId);
            Assert.Equal(LinkType.Tcp, proxies[0].LinkType);
            Assert.Equal(25566, proxies[0].RemotePort);
            Assert.Equal("web", proxies[1].ProxyId);
            Assert.Equal(LinkType.Http, proxies[1].LinkType);
            Assert.Equal("web.example.com", proxies[1].Domain);
            Assert.Equal("web-group", proxies[1].Group); // 分组解析
            Assert.Equal("mc-udp", proxies[2].ProxyId);
            Assert.Equal(LinkType.Udp, proxies[2].LinkType);
            Assert.Null(proxies[2].Group); // 未填分组 → null（ATS 端显示 default）
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void LoadAgentConfig_AutoGenerateClientId_WhenEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("atc-cfg-test");
        try
        {
            var path = Path.Combine(dir.FullName, "agent.toml");
            File.WriteAllText(path, """
                serverAddr = "1.2.3.4"
                serverPort = 7000
                token = "t"

                [[tunnels]]
                name = "p1"
                type = "tcp"
                localPort = 25565
                remotePort = 25566
                """);

            var cfg = ConfigLoader.LoadAgentConfig(path)!;
            Assert.Equal("", cfg.ClientId);

            var opts1 = ConfigLoader.ToAgentOptions(cfg);
            var opts2 = ConfigLoader.ToAgentOptions(cfg);

            // 自动生成：agent-主机名-4位随机（两次调用不同，避免重复）
            Assert.StartsWith($"agent-{Environment.MachineName}-", opts1.ClientId);
            Assert.NotEqual(opts1.ClientId, opts2.ClientId);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}
