using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// UDP 隧道监听（FR-031）：UdpClient 绑定隧道端口。
/// - 注册时建立一条固定数据通道并发送 OpenTunnel；
/// - 收到远端 UDP 包 → 封装为数据帧送通道 → Agent 转发本地；
/// - Agent 回传帧 → 发送给最近一次远端来源（单客户端场景）。
/// </summary>
public sealed class UdpProxyListener : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly ChannelMultiplexer _mux;
    private readonly string _proxyId;
    private readonly Aeterni.Tunnel.Engine.Traffic.TrafficCounter _traffic;
    private readonly CancellationTokenSource _cts = new();
    private Channel? _channel;
    private IPEndPoint? _lastRemote;

    public int Port { get; }

    public UdpProxyListener(ChannelMultiplexer mux, string proxyId, int port, Aeterni.Tunnel.Engine.Traffic.TrafficCounter traffic)
    {
        _mux = mux;
        _proxyId = proxyId;
        Port = port;
        _traffic = traffic;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    public void Start()
    {
        _channel = _mux.OpenChannel();
        _ = _mux.SendControlAsync(MessageCodec.Serialize(new OpenTunnelMessage(_proxyId, _channel.ChannelId)));
        _ = ReceiveLoopAsync();
        _ = ChannelReadLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                _lastRemote = result.RemoteEndPoint;
                _traffic.AddUp(result.Buffer.Length);
                if (_channel is not null)
                    await _channel.WriteAsync(result.Buffer, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 连接断开 */ }
    }

    /// <summary>Agent 回传的数据帧 → 发给最近一次远端来源</summary>
    private async Task ChannelReadLoopAsync()
    {
        try
        {
            while (_channel is not null && !_cts.IsCancellationRequested)
            {
                var data = await _channel.ReadAsync(_cts.Token);
                if (data is null)
                    break;
                _traffic.AddDown(data.Length);
                if (_lastRemote is not null)
                    await _udp.SendAsync(data, _lastRemote);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
