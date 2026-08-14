using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol.Messages;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// HTTP vhost 监听（FR-032）：监听 vhostHTTPPort，按请求 Host 头路由到对应代理，
/// 建立隧道后把请求连同剩余流量透传到本地服务（不终结 HTTP）。
/// </summary>
public sealed class VhostHttpListener : IAsyncDisposable, IVhostRegistry
{
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (ChannelMultiplexer Mux, string ProxyId)> _routes = new();

    /// <summary>日志（调试用）</summary>
    public event Action<string>? LogLine;

    public int Port { get; }

    public VhostHttpListener(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public void Register(string host, ChannelMultiplexer mux, string proxyId)
        => _routes[host] = (mux, proxyId);

    public void Unregister(string host)
        => _routes.TryRemove(host, out _);

    /// <summary>是否已路由该 host（管理端/测试用）</summary>
    public bool Contains(string host)
        => _routes.ContainsKey(host);

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
            var stream = new BufferedStream(client.GetStream());
            var headerBytes = await ReadHeaderAsync(stream, _cts.Token);
            var host = ParseHost(Encoding.ASCII.GetString(headerBytes));
            LogLine?.Invoke($"vhost 连接：host={host ?? "(null)"} 头 {headerBytes.Length}B");

            if (host is null || !_routes.TryGetValue(host, out var route))
            {
                LogLine?.Invoke($"vhost 未匹配：{host ?? "(null)"} → 404");
                await WriteNotFoundAsync(stream);
                return;
            }

            LogLine?.Invoke($"vhost 命中：{host} → {route.ProxyId}");
            var ch = route.Mux.OpenChannel();
            await route.Mux.SendControlAsync(MessageCodec.Serialize(new OpenTunnelMessage(route.ProxyId, ch.ChannelId)));

            // 先显式转发已读的请求头（ReadByte 已从缓冲取出），再转发剩余流量
            await ch.WriteAsync(headerBytes);
            await TcpBridge.RunAsync(stream, ch);
        }
        catch (Exception ex) { LogLine?.Invoke($"vhost 连接异常：{ex.Message}"); }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// 读取请求头原始字节（到 \r\n\r\n），最多 MaxHeaderBytes。
    /// 用 ReadByte 逐字节读：ReadByte 读入的数据进入 BufferedStream 内部缓冲，
    /// 其中未消费的字节（\r\n\r\n 之后的部分）保留在缓冲中，由 TcpBridge 继续转发；
    /// 已消费的请求头字节由调用方显式转发到通道。
    /// </summary>
    private static async Task<byte[]> ReadHeaderAsync(BufferedStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream(MaxHeaderBytes);
        while (ms.Length < MaxHeaderBytes)
        {
            ct.ThrowIfCancellationRequested();
            var b = stream.ReadByte();
            if (b < 0)
                break;
            ms.WriteByte((byte)b);
            if (ms.Length >= 4 &&
                ms.GetBuffer()[ms.Length - 4] == '\r' && ms.GetBuffer()[ms.Length - 3] == '\n' &&
                ms.GetBuffer()[ms.Length - 2] == '\r' && ms.GetBuffer()[ms.Length - 1] == '\n')
                break;
        }
        return ms.ToArray();
    }

    /// <summary>解析 Host 头（去端口；失败返回 null）</summary>
    private static string? ParseHost(string header)
    {
        foreach (var line in header.Split("\r\n"))
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                var host = line["Host:".Length..].Trim();
                if (host.StartsWith('['))
                    return host;
                var colon = host.LastIndexOf(':');
                return colon > 0 ? host[..colon] : host;
            }
        }
        return null;
    }

    private static async Task WriteNotFoundAsync(Stream stream)
    {
        var body = "404 Not Found (Aeterni Tunnel)";
        var resp = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 404 Not Found\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(resp);
        await stream.FlushAsync();
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
