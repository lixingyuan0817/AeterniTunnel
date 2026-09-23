using DataChannelDotnet;
using DataChannelDotnet.Data;

namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// Engine boundary for the selected libdatachannel wrapper. Signaling and ICE
/// remain outside this type: the host feeds the negotiated channel here after
/// the existing ATS Peer signaling exchange has authorized both identities.
/// </summary>
public sealed class NativePeerDataChannelAdapter : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PeerDataPlaneGate _dataPlaneGate;
    private readonly PeerSessionLifecycle _session;
    private IRtcDataChannel? _channel;
    private bool _open;
    private bool _disposed;

    public NativePeerDataChannelAdapter(
        PeerSessionLifecycle session,
        PeerDataPlaneGate dataPlaneGate)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dataPlaneGate = dataPlaneGate ?? throw new ArgumentNullException(nameof(dataPlaneGate));
        if (_session.State == CommunicationSessionState.Created)
            _session.BeginConnect();
    }

    public bool IsOpen
    {
        get { lock (_gate) return _open && !_disposed; }
    }

    public event Action<ReadOnlyMemory<byte>>? PayloadReceived;

    public event Action? Opened;

    public event Action? Closed;

    public bool Authenticate(
        string localPeerId,
        string remotePeerId,
        string serviceId,
        string token,
        DateTimeOffset now)
        => _dataPlaneGate.Authenticate(localPeerId, remotePeerId, serviceId, token, now);

    public void Attach(IRtcDataChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channel is not null)
                throw new InvalidOperationException("A peer adapter can attach only one data channel.");
            _channel = channel;
        }

        channel.OnOpen += _ => MarkOpen();
        channel.OnTextReceivedSafe += (_, message) => HandleIncoming(System.Text.Encoding.UTF8.GetBytes(message.Text));
        channel.OnBinaryReceivedSafe += (_, payload) => HandleIncoming(payload.Data.ToArray());
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IRtcDataChannel channel;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_open || _channel is null)
                throw new CommunicationException(CommunicationErrorCode.Closed, "Peer data channel is not open.");
            if (_dataPlaneGate.State != PeerDataPlaneState.Authenticated)
                throw new CommunicationException(CommunicationErrorCode.Unauthorized, "Peer data channel is not authenticated.");
            channel = _channel;
        }

        channel.Send(payload.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        bool close;
        lock (_gate)
        {
            close = !_disposed;
            _disposed = true;
            _open = false;
        }

        if (!close)
            return;
        _dataPlaneGate.Close();
        await _session.CloseAsync("native peer adapter disposed");
        Closed?.Invoke();
    }

    private void MarkOpen()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _open = true;
        }

        _session.MarkConnected();
        Opened?.Invoke();
    }

    private void HandleIncoming(ReadOnlyMemory<byte> payload)
    {
        _dataPlaneGate.TryDeliver(payload, data => PayloadReceived?.Invoke(data));
    }
}
