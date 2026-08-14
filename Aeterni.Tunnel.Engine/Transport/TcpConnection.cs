using System.IO;
using System.Net.Sockets;

namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// TCP 连接封装：持有 TcpClient 与已就绪的字节流。
/// </summary>
internal sealed class TcpConnection : ITunnelConnection
{
    private readonly TcpClient _tcp;

    public Stream Stream { get; }

    public string RemoteEndPoint { get; }

    public TcpConnection(TcpClient tcp, Stream stream, string remoteEndPoint)
    {
        _tcp = tcp;
        Stream = stream;
        RemoteEndPoint = remoteEndPoint;
    }

    public ValueTask DisposeAsync()
    {
        Stream.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
