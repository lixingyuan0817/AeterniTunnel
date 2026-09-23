namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>通信层支持的传输语义；业务层不得把数据报当作可靠流使用。</summary>
[Flags]
public enum TransportCapabilities : ulong
{
    None = 0,
    ReliableStream = 1UL << 0,
    ReliableMessage = 1UL << 1,
    RealtimeDatagram = 1UL << 2,
    PeerSession = 1UL << 3,
}

public enum CommunicationErrorCode
{
    Unknown = 0,
    Cancelled,
    Closed,
    Timeout,
    Unauthorized,
    PayloadTooLarge,
    BackpressureLimit,
    UnsupportedCapability,
    ProtocolViolation,
    TransportFailure,
}

public enum CommunicationSessionState
{
    Created = 0,
    Connecting,
    Connected,
    Degraded,
    Closing,
    Closed,
    Failed,
}

public sealed class CommunicationException : Exception
{
    public CommunicationErrorCode Code { get; }

    public CommunicationException(CommunicationErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;
}

/// <summary>可靠流：有序、完整、背压；关闭方向和连接错误必须可观察。</summary>
public interface IReliableStream
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    ValueTask CompleteWritesAsync(CancellationToken ct = default);
}

/// <summary>可靠消息：保留消息边界并受最大长度限制，不承诺跨重连 exactly-once。</summary>
public interface IReliableMessageChannel
{
    int MaxMessageLength { get; }
    int MaxQueuedMessages { get; }
    long MaxQueuedBytes { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>实时数据报：保留包边界，允许丢失/乱序，必须有大小、有效期和队列上限。</summary>
public interface IRealtimeDatagramChannel
{
    int MaxDatagramLength { get; }
    int MaxQueuedDatagrams { get; }
    long MaxQueuedBytes { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, TimeSpan lifetime, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>已授权 Peer 会话的最小状态边界；不包含房间、用户或音频业务模型。</summary>
public interface IPeerSession : IAsyncDisposable
{
    string LocalPeerId { get; }
    string RemotePeerId { get; }
    CommunicationSessionState State { get; }
    TransportCapabilities Capabilities { get; }
    ValueTask CloseAsync(string reason, CancellationToken ct = default);
}

/// <summary>授权提供者：默认拒绝，宿主负责把业务权限映射到通信服务。</summary>
public interface ICommunicationAuthorizationProvider
{
    ValueTask<bool> AuthorizeAsync(string localPeerId, string remotePeerId, string serviceId, CancellationToken ct = default);
}

public sealed class DenyAllCommunicationAuthorizationProvider : ICommunicationAuthorizationProvider
{
    public ValueTask<bool> AuthorizeAsync(string localPeerId, string remotePeerId, string serviceId, CancellationToken ct = default)
        => ValueTask.FromResult(false);
}

/// <summary>传输工厂；连接方向与传输能力由宿主显式选择。</summary>
public interface ITransportFactory
{
    TransportCapabilities Capabilities { get; }
    ValueTask<ITunnelConnection> ConnectAsync(string host, int port, CancellationToken ct = default);
    ValueTask<ITunnelConnection> AcceptAsync(CancellationToken ct = default);
}
