using System.Net;
using System.Text;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Transport;
using Aeterni.Tunnel.Engine.Wire;

namespace Aeterni.Tunnel.Engine.Tests;

public class ChannelMultiplexerTests
{
    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<(ChannelMultiplexer Server, ChannelMultiplexer Client)> ConnectPairAsync()
    {
        var port = FreePort();
        var serverTransport = TcpTlsTransport.Server(IPAddress.Loopback, port);
        var clientTransport = TcpTlsTransport.Client("127.0.0.1", port, useTls: false);

        // 先发起 client 连接（进入 listener backlog），再 accept，避免 Task.Run 嵌套
        var clientTask = clientTransport.ConnectAsync("127.0.0.1", port).AsTask();
        var serverConn = await serverTransport.AcceptAsync();
        var clientConn = await clientTask;

        var serverMux = new ChannelMultiplexer(serverConn);
        var clientMux = new ChannelMultiplexer(clientConn);
        serverMux.Start();
        clientMux.Start();
        return (serverMux, clientMux);
    }

    [Fact]
    public async Task BidirectionalData_OnOneChannel()
    {
        var (server, client) = await ConnectPairAsync();
        await using var _ = server;
        await using var _2 = client;

        // client 本端分配 id，server 模拟收到 OpenTunnel 后按同 id 建通道
        var clientCh = client.OpenChannel();
        var serverCh = server.AcceptChannel(clientCh.ChannelId);

        var payload = Encoding.UTF8.GetBytes("hello-channel");
        await clientCh.WriteAsync(payload);

        var received = await serverCh.ReadAsync();
        Assert.Equal(payload, received);

        var echo = Encoding.UTF8.GetBytes("echo-back");
        await serverCh.WriteAsync(echo);
        Assert.Equal(echo, await clientCh.ReadAsync());
    }

    [Fact]
    public async Task MultipleChannels_AreIsolated()
    {
        var (server, client) = await ConnectPairAsync();
        await using var _ = server;
        await using var _2 = client;

        var c1 = client.OpenChannel();
        var s1 = server.AcceptChannel(c1.ChannelId);
        var c2 = client.OpenChannel();
        var s2 = server.AcceptChannel(c2.ChannelId);
        var c3 = client.OpenChannel();
        var s3 = server.AcceptChannel(c3.ChannelId);

        Assert.NotEqual(c1.ChannelId, c2.ChannelId);
        Assert.NotEqual(c2.ChannelId, c3.ChannelId);

        var payload1 = Encoding.UTF8.GetBytes("chan-1-data");
        var payload2 = Encoding.UTF8.GetBytes("chan-2-data");
        var payload3 = Encoding.UTF8.GetBytes("chan-3-data");

        await c1.WriteAsync(payload1);
        await c2.WriteAsync(payload2);
        await c3.WriteAsync(payload3);

        Assert.Equal(payload1, await s1.ReadAsync());
        Assert.Equal(payload2, await s2.ReadAsync());
        Assert.Equal(payload3, await s3.ReadAsync());
    }

    [Fact]
    public async Task CloseChannel_PeerReadsNull()
    {
        var (server, client) = await ConnectPairAsync();
        await using var _ = server;
        await using var _2 = client;

        var clientCh = client.OpenChannel();
        var serverCh = server.AcceptChannel(clientCh.ChannelId);

        await clientCh.CloseAsync();

        Assert.Null(await serverCh.ReadAsync());
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPong()
    {
        // server 端用 multiplexer（读循环自动回 Pong），client 端裸连接发 Ping
        var port = FreePort();
        var serverTransport = TcpTlsTransport.Server(IPAddress.Loopback, port);
        var clientTransport = TcpTlsTransport.Client("127.0.0.1", port, useTls: false);

        var clientTask = clientTransport.ConnectAsync("127.0.0.1", port).AsTask();
        var serverConn = await serverTransport.AcceptAsync();
        var clientConn = await clientTask;

        var serverMux = new ChannelMultiplexer(serverConn);
        serverMux.Start();

        await FrameCodec.WriteAsync(clientConn.Stream, new Frame(FrameType.Ping, 0, [0x01]));
        var pong = await FrameCodec.ReadAsync(clientConn.Stream);

        Assert.Equal(FrameType.Pong, pong.Type);
        Assert.Equal([0x01], pong.Payload);

        await serverMux.DisposeAsync();
        await clientConn.DisposeAsync();
        await serverTransport.DisposeAsync();
    }
}
