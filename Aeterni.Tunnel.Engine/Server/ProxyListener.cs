using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 服务端代理端口监听：用户连接 → 分配通道 → 发 OpenTunnel 给 Agent → 双向转发。
/// </summary>
public sealed class ProxyListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ChannelMultiplexer _mux;
    private readonly string _proxyId;
    private readonly Aeterni.Tunnel.Engine.Traffic.TrafficCounter _traffic;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public ProxyListener(ChannelMultiplexer mux, string proxyId, int port, Aeterni.Tunnel.Engine.Traffic.TrafficCounter traffic)
    {
        _mux = mux;
        _proxyId = proxyId;
        Port = port;
        _traffic = traffic;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public void Start()
    {
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var userTcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleUserAsync(userTcp);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleUserAsync(TcpClient userTcp)
    {
        try
        {
            var ch = _mux.OpenChannel();
            await _mux.SendControlAsync(MessageCodec.Serialize(new OpenTunnelMessage(_proxyId, ch.ChannelId)));
            await TcpBridge.RunAsync(userTcp.GetStream(), ch, _traffic.AddUp, _traffic.AddDown);
        }
        catch { /* 用户连接异常，直接关闭 */ }
        finally
        {
            userTcp.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
