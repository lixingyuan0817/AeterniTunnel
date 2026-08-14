using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Client;

namespace Aeterni.Tunnel.Engine.Tests;

public class HealthCheckerTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>找一个当前确认不可连接（未监听）的端口，避免 TIME_WAIT 误判</summary>
    private static int UnusedPort()
    {
        for (var i = 0; i < 50; i++)
        {
            var port = Random.Shared.Next(40000, 60000);
            using var c = new TcpClient();
            try
            {
                c.Connect(IPAddress.Loopback, port);
            }
            catch
            {
                return port; // 连接失败 = 未监听
            }
        }
        throw new InvalidOperationException("未找到不可连接端口");
    }

    [Fact]
    public async Task TcpProbe_UnreachableThenRecover()
    {
        var port = UnusedPort(); // 初始未监听（已确认不可连接）

        // 间隔 1s、连续失败 2 次判定不健康
        var checker = new HealthChecker("127.0.0.1", port,
            new HealthCheckOptions("tcp", null, IntervalSeconds: 1, TimeoutSeconds: 1, MaxFailed: 2));
        var changes = new List<bool>();
        checker.StatusChanged += ok => changes.Add(ok);

        checker.Start();
        // 异步 connect 到未监听端口会挂起至超时（1s），判定需 2 次失败 ≈ 4s，故等待 5s
        await Task.Delay(5000);
        Assert.False(checker.IsHealthy);
        Assert.Contains(false, changes);

        // 起服务 → 应恢复健康（探测成功较快）
        var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        _ = AcceptAndDiscardAsync(server);
        await Task.Delay(4000);
        Assert.True(checker.IsHealthy);
        Assert.Contains(true, changes);

        await checker.DisposeAsync();
        server.Stop();
    }

    [Fact]
    public async Task HttpProbe_Non2xx_IsUnhealthy()
    {
        var port = FreePort();
        var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        _ = Http404Async(server);

        var checker = new HealthChecker("127.0.0.1", port,
            new HealthCheckOptions("http", "/health", IntervalSeconds: 0, TimeoutSeconds: 1, MaxFailed: 1));
        var unhealthy = false;
        checker.StatusChanged += ok => { if (!ok) unhealthy = true; };

        checker.Start();
        await Task.Delay(2500); // 间隔 1s + 1 次失败判定
        Assert.False(checker.IsHealthy);
        Assert.True(unhealthy);

        await checker.DisposeAsync();
        server.Stop();
    }

    private static async Task AcceptAndDiscardAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                client.Dispose();
            }
        }
        catch { }
    }

    private static async Task Http404Async(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var buf = new byte[1024];
                            await client.GetStream().ReadAsync(buf);
                            var body = "404 Not Found";
                            var resp = $"HTTP/1.1 404 Not Found\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                            await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(resp));
                        }
                        catch { }
                    }
                });
            }
        }
        catch { }
    }
}
