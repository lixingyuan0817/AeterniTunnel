using System.IO;

namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// 一条已建立的传输连接（已鉴权/加密的字节流）。
/// </summary>
public interface ITunnelConnection : IAsyncDisposable
{
    /// <summary>可读写的字节流（TCP 裸流或 SslStream / QUIC Stream 等）</summary>
    Stream Stream { get; }

    /// <summary>对端地址描述（日志用）</summary>
    string RemoteEndPoint { get; }
}
