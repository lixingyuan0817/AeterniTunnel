namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// 可插拔传输（决策 AD-004）：
///   默认 TcpTlsTransport（TCP + TLS1.3）；
///   QUIC / WSS 作为后续可插拔实现，协议层不感知差异。
/// </summary>
public interface ITunnelTransport : ITransportFactory
{
    /// <summary>"tcp" / "tcp+tls" / "quic" / "wss"</summary>
    string Name { get; }

}
