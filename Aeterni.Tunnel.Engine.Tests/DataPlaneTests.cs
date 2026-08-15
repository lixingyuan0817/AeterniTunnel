using System.Net;
using System.Net.Sockets;
using System.Text;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

public class DataPlaneTests
{
    private const string TestToken = "test-secret-token";

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>本地 echo 服务（后台 accept + echo）</summary>
    private static TcpListener StartEchoServer(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = EchoLoopAsync(listener);
        return listener;
    }

    private static async Task EchoLoopAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = EchoClientAsync(client);
            }
        }
        catch { }
    }

    private static async Task EchoClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buf = new byte[4096];
                int n;
                while ((n = await stream.ReadAsync(buf)) > 0)
                    await stream.WriteAsync(buf.AsMemory(0, n));
            }
            catch { }
        }
    }

    private static async Task EchoUdpLoopAsync(UdpClient udp)
    {
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync();
                await udp.SendAsync(r.Buffer, r.RemoteEndPoint);
            }
        }
        catch { }
    }

    [Fact]
    public async Task UdpProxy_EndToEnd_Echo()
    {
        using var echoUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echoUdp.Client.LocalEndPoint).Port;
        _ = EchoUdpLoopAsync(echoUdp);

        var proxyPort = FreePort();
        var (listener, agent) = await SetupTunnelAsync(echoPort, proxyPort, LinkType.Udp);
        await using var _l = listener;
        await using var _a = agent;

        // 用户 UDP 发包 → 穿透 → 本地 UDP echo 回
        using var user = new UdpClient();
        var payload = Encoding.UTF8.GetBytes("udp-ping-1");
        await user.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, proxyPort));
        var resp = await user.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, resp.Buffer);
    }

    private static async Task<(ServerListener Listener, AgentSession Agent)> SetupTunnelAsync(int echoPort, int proxyPort, LinkType linkType = LinkType.Tcp)
    {
        var serverPort = FreePort();
        var listener = new ServerListener(serverPort, TestToken);
        listener.Start();

        var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentSession(new AgentOptions(
            ServerAddr: "127.0.0.1",
            ServerPort: serverPort,
            Token: TestToken,
            ClientId: "agent-e2e",
            HeartbeatInterval: TimeSpan.FromMilliseconds(500)));
        agent.LogLine += s => loginTcs.TrySetResult(s);
        await agent.ConnectAsync();
        await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var regTcs = new TaskCompletionSource<(string, bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.ProxyRegistered += (id, ok, addr) => regTcs.TrySetResult((id, ok, addr));
        await agent.RegisterProxyAsync("proxy-e2e", linkType, "127.0.0.1", echoPort, remotePort: proxyPort);
        var (id, ok, remoteAddr) = await regTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ok, remoteAddr);

        return (listener, agent);
    }

    [Fact]
    public async Task TcpProxy_EndToEnd_Echo()
    {
        using var echoListener = StartEchoServer(out var echoPort);
        var proxyPort = FreePort();

        var (listener, agent) = await SetupTunnelAsync(echoPort, proxyPort);
        await using var _ = listener;
        await using var _2 = agent;

        // 用户连接 Server 的隧道端口，发数据 → echo 回来
        using var user = new TcpClient();
        await user.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = user.GetStream();

        var payload = Encoding.UTF8.GetBytes("ping-123456");
        await stream.WriteAsync(payload);

        var buf = new byte[256];
        var n = await stream.ReadAsync(buf);
        Assert.Equal(payload, buf.AsSpan(0, n).ToArray());

        // 流量统计：穿透后隧道应有非零收发字节
        var traffic = agent.GetTrafficSnapshot();
        Assert.True(traffic.TryGetValue("proxy-e2e", out var tr), "应有该隧道的流量记录");
        Assert.True(tr.Up + tr.Down > 0, $"穿透后流量应非零（up={tr.Up} down={tr.Down}）");
    }

    [Fact]
    public async Task TcpProxy_MultipleUsers_Isolated()
    {
        using var echoListener = StartEchoServer(out var echoPort);
        var proxyPort = FreePort();

        var (listener, agent) = await SetupTunnelAsync(echoPort, proxyPort);
        await using var _ = listener;
        await using var _2 = agent;

        async Task<string> RoundTripAsync(string payload)
        {
            using var user = new TcpClient();
            await user.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = user.GetStream();
            var data = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(data);
            var buf = new byte[512];
            var n = await stream.ReadAsync(buf);
            return Encoding.UTF8.GetString(buf, 0, n);
        }

        var p1 = await RoundTripAsync("user-1-hello");
        var p2 = await RoundTripAsync("user-2-hello");
        var p3 = await RoundTripAsync("user-3-hello");

        Assert.Equal("user-1-hello", p1);
        Assert.Equal("user-2-hello", p2);
        Assert.Equal("user-3-hello", p3);
    }
}
