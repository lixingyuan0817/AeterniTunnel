namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// 可插拔传输（决策 AD-004）：
///   默认 TcpTlsTransport（TCP + TLS1.3）；
///   QUIC / WSS 作为后续可插拔实现，协议层不感知差异。
/// </summary>
public interface ITunnelTransport
{
    /// <summary>"tcp" / "tcp+tls" / "quic" / "wss"</summary>
    string Name { get; }

    /// <summary>客户端侧：连接远端</summary>
    ValueTask<ITunnelConnection> ConnectAsync(string host, int port, CancellationToken ct = default);

    /// <summary>服务端侧：接受一个连接</summary>
    ValueTask<ITunnelConnection> AcceptAsync(CancellationToken ct = default);
}
