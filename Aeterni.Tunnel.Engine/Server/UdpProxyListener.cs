using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// UDP 隧道监听（FR-031）：UdpClient 绑定隧道端口。
/// - 注册时建立一条固定数据通道并发送 OpenTunnel；
/// - 收到远端 UDP 包 → 封装为数据帧送通道 → Agent 转发本地；
/// - Agent 回传帧 → 按来源关联 ID 发送给对应远端来源；旧无 ID 数据兼容最近来源。
/// </summary>
public sealed class UdpProxyListener : IAsyncDisposable
{
    private const int DefaultMaxSources = 1024;
    private static readonly TimeSpan DefaultSourceIdleTimeout = TimeSpan.FromMinutes(2);
    private readonly UdpClient _udp;
    private readonly ChannelMultiplexer _mux;
    private readonly string _proxyId;
    private readonly Aeterni.Tunnel.Engine.Traffic.TrafficCounter _traffic;
    private readonly bool _useSourceAssociation;
    private readonly CancellationTokenSource _cts = new();
    private Channel? _channel;
    private readonly UdpSourceRegistry _sources;
    private readonly object _sourcesLock = new();
    private IPEndPoint? _legacyLastRemote;

    public int Port { get; }

    public UdpProxyListener(ChannelMultiplexer mux, string proxyId, int port, Aeterni.Tunnel.Engine.Traffic.TrafficCounter traffic, bool useSourceAssociation = true, int maxSources = DefaultMaxSources, TimeSpan? sourceIdleTimeout = null)
    {
        _mux = mux;
        _proxyId = proxyId;
        Port = port;
        _traffic = traffic;
        _useSourceAssociation = useSourceAssociation;
        _sources = new UdpSourceRegistry(maxSources, sourceIdleTimeout ?? DefaultSourceIdleTimeout);
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
                lock (_sourcesLock)
                    _legacyLastRemote = result.RemoteEndPoint;
                var sourceId = _useSourceAssociation ? GetOrCreateSourceId(result.RemoteEndPoint) : 0;
                _traffic.AddUp(result.Buffer.Length);
                if (_channel is not null)
                    await _channel.WriteAsync(_useSourceAssociation
                        ? UdpSourceEnvelope.Encode(sourceId, result.Buffer)
                        : result.Buffer, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 连接断开 */ }
    }

    /// <summary>Agent 回传的数据帧 → 根据关联 ID 发给对应来源。</summary>
    private async Task ChannelReadLoopAsync()
    {
        try
        {
            while (_channel is not null && !_cts.IsCancellationRequested)
            {
                var data = await _channel.ReadAsync(_cts.Token);
                if (data is null)
                    break;
                if (!_useSourceAssociation)
                {
                    IPEndPoint? fallback;
                    lock (_sourcesLock)
                        fallback = _legacyLastRemote;
                    if (fallback is not null)
                    {
                        _traffic.AddDown(data.Length);
                        await _udp.SendAsync(data, fallback);
                    }
                    continue;
                }

                if (!UdpSourceEnvelope.TryDecode(data, out var sourceId, out var payload))
                    continue;

                if (_sources.TryGet(sourceId, out var remote) && remote is not null)
                {
                    _traffic.AddDown(payload.Length);
                    await _udp.SendAsync(payload, remote);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (_channel is not null)
                await _channel.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        _sources.Clear();
        if (_channel is not null)
            await _channel.CloseAsync();
    }

    private uint GetOrCreateSourceId(IPEndPoint endpoint) => _sources.GetOrCreate(endpoint);
}
