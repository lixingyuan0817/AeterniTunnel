using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// HTTPS vhost 监听（FR-033）：监听 vhostHTTPSPort，读 TLS ClientHello 的 SNI 路由到对应代理。
/// TLS 流量透传（不终结），已读的记录头 + ClientHello 显式转发到通道。
/// </summary>
public sealed class VhostHttpsListener : IAsyncDisposable, IVhostRegistry
{
    private const int RecordHeaderLength = 5;
    private const int MaxRecordLength = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (ChannelMultiplexer Mux, string ProxyId)> _routes = new();

    /// <summary>日志（调试用）</summary>
    public event Action<string>? LogLine;

    public int Port { get; }

    public VhostHttpsListener(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public void Register(string host, ChannelMultiplexer mux, string proxyId)
        => _routes[host] = (mux, proxyId);

    public void Unregister(string host)
        => _routes.TryRemove(host, out _);

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
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleConnectionAsync(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();

            // 读 TLS 记录头（5 字节）
            var header = new byte[RecordHeaderLength];
            await stream.ReadExactlyAsync(header, _cts.Token);

            if (header[0] != 0x16) // 非 Handshake 记录，直接关闭
            {
                LogLine?.Invoke("https: 非 TLS 流量，拒绝");
                return;
            }

            var recordLen = (header[3] << 8) | header[4];
            if (recordLen <= 0 || recordLen > MaxRecordLength)
            {
                LogLine?.Invoke($"https: 非法记录长度 {recordLen}");
                return;
            }

            var clientHello = new byte[recordLen];
            await stream.ReadExactlyAsync(clientHello, _cts.Token);
            var sni = SniParser.ParseClientHello(clientHello);
            LogLine?.Invoke($"https: SNI={sni ?? "(null)"}");

            if (sni is null || !_routes.TryGetValue(sni, out var route))
            {
                LogLine?.Invoke($"https: 未匹配 {sni ?? "(null)"}，关闭");
                return;
            }

            LogLine?.Invoke($"https: 命中 {sni} → {route.ProxyId}");
            var ch = route.Mux.OpenChannel();
            await route.Mux.SendControlAsync(MessageCodec.Serialize(new OpenTunnelMessage(route.ProxyId, ch.ChannelId)));

            // 转发已读的 TLS 记录头 + ClientHello，再透传剩余 TLS 流量
            var head = new byte[RecordHeaderLength + clientHello.Length];
            header.CopyTo(head, 0);
            clientHello.CopyTo(head, RecordHeaderLength);
            await ch.WriteAsync(head);
            await TcpBridge.RunAsync(stream, ch);
        }
        catch (Exception ex) { LogLine?.Invoke($"https: 连接异常 {ex.Message}"); }
        finally
        {
            client.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
