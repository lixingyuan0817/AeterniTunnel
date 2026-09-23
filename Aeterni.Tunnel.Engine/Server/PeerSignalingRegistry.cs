using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Server;

internal interface IPeerSignalEndpoint
{
    string PeerId { get; }
    ValueTask SendPeerSignalAsync(Message message);
}

/// <summary>
/// ATS 单一控制入口上的短期 Peer 信令租约。只转发已授权的 SDP/ICE，
/// 不解释业务负载，也不提供任意目标探测或开放中继。
/// </summary>
internal sealed class PeerSignalingRegistry
{
    private static readonly TimeSpan MaxLease = TimeSpan.FromSeconds(30);
    private const int MaxRequestIdLength = 96;
    private const int MaxServiceIdLength = 128;
    private const int MaxSdpLength = 256 * 1024;
    private const int MaxCandidateLength = 4096;
    private const int MaxCandidatesPerLease = 128;

    private sealed record Lease(string RequestId, IPeerSignalEndpoint Requester,
        IPeerSignalEndpoint Target, string ServiceId, PeerAuthorizationLease Authorization, int CandidateCount);

    private readonly ICommunicationAuthorizationProvider _authorization;
    private readonly object _gate = new();
    private readonly Dictionary<string, IPeerSignalEndpoint> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public PeerSignalingRegistry(ICommunicationAuthorizationProvider? authorization = null)
        => _authorization = authorization ?? new DenyAllCommunicationAuthorizationProvider();

    public void Register(IPeerSignalEndpoint endpoint)
    {
        lock (_gate)
            _peers[endpoint.PeerId] = endpoint;
    }

    public void Unregister(IPeerSignalEndpoint endpoint)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(endpoint.PeerId, out var current) && ReferenceEquals(current, endpoint))
                _peers.Remove(endpoint.PeerId);
            foreach (var id in _leases.Where(x => ReferenceEquals(x.Value.Requester, endpoint) || ReferenceEquals(x.Value.Target, endpoint))
                         .Select(x => x.Key).ToArray())
                _leases.Remove(id);
        }
    }

    public async Task HandleRequestAsync(IPeerSignalEndpoint requester, PeerRequestMessage request)
    {
        if (!ValidRequest(request, out var validationError))
        {
            await RejectRequestAsync(requester, request.RequestId, validationError);
            return;
        }

        IPeerSignalEndpoint? target;
        lock (_gate)
        {
            PruneExpiredLocked();
            _peers.TryGetValue(request.TargetPeerId, out target);
            if (target is null || string.Equals(target.PeerId, requester.PeerId, StringComparison.Ordinal))
            {
                target = null;
            }
            else if (_leases.ContainsKey(request.RequestId))
            {
                target = null;
                validationError = "requestId 已存在";
            }
            else if (_leases.Values.Count(x => ReferenceEquals(x.Requester, requester) || ReferenceEquals(x.Target, requester)) >= 8)
            {
                target = null;
                validationError = "Peer 请求数超过上限";
            }
        }

        if (target is null)
        {
            await RejectRequestAsync(requester, request.RequestId,
                string.IsNullOrEmpty(validationError) ? "目标 Peer 不在线" : validationError);
            return;
        }

        bool allowed;
        try
        {
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(request.ExpiresUnixMilliseconds);
            allowed = await _authorization.AuthorizeAsync(requester.PeerId, target.PeerId, request.ServiceId) &&
                      await _authorization.AuthorizeAsync(target.PeerId, requester.PeerId, request.ServiceId);
            if (!allowed || expires <= DateTimeOffset.UtcNow)
            {
                await RejectRequestAsync(requester, request.RequestId, "Peer 授权被拒绝或已过期");
                return;
            }
        }
        catch
        {
            await RejectRequestAsync(requester, request.RequestId, "Peer 授权检查失败");
            return;
        }

        var issuedAt = DateTimeOffset.UtcNow;
        var leaseExpires = issuedAt.Add(MaxLease);
        var requestedExpires = DateTimeOffset.FromUnixTimeMilliseconds(request.ExpiresUnixMilliseconds);
        if (requestedExpires <= issuedAt)
        {
            await RejectRequestAsync(requester, request.RequestId, "Peer 授权租约已过期");
            return;
        }

        if (requestedExpires < leaseExpires)
            leaseExpires = requestedExpires;
        var authorization = PeerAuthorizationLease.Create(
            requester.PeerId, target.PeerId, request.ServiceId, issuedAt, leaseExpires - issuedAt);
        var lease = new Lease(request.RequestId, requester, target, request.ServiceId, authorization, 0);
        lock (_gate)
            _leases[request.RequestId] = lease;

        await requester.SendPeerSignalAsync(new PeerRequestAckMessage(
            request.RequestId, true, null, leaseExpires.ToUnixTimeMilliseconds(), authorization.Token));
        await target.SendPeerSignalAsync(new PeerRequestNoticeMessage(
            request.RequestId, requester.PeerId, request.ServiceId, leaseExpires.ToUnixTimeMilliseconds(), authorization.Token));
    }

    public async Task HandleDescriptionAsync(IPeerSignalEndpoint sender, PeerDescriptionMessage description)
    {
        var lease = FindLease(description.RequestId);
        if (lease is null || !ValidSdp(description.Sdp))
        {
            await SendSignalAckAsync(sender, description.RequestId, "description", false,
                lease is null ? "租约不存在或已过期" : "SDP 超过限制");
            return;
        }

        var isRequester = ReferenceEquals(sender, lease.Requester);
        if (description.IsOffer != isRequester)
        {
            await SendSignalAckAsync(sender, description.RequestId, "description", false, "SDP 方向不匹配");
            return;
        }

        await (isRequester ? lease.Target : lease.Requester).SendPeerSignalAsync(description);
        await SendSignalAckAsync(sender, description.RequestId, "description", true, null);
    }

    public async Task HandleCandidateAsync(IPeerSignalEndpoint sender, PeerCandidateMessage candidate)
    {
        var lease = FindLease(candidate.RequestId);
        if (lease is null || (!candidate.EndOfCandidates &&
            (string.IsNullOrWhiteSpace(candidate.Candidate) || candidate.Candidate.Length > MaxCandidateLength)) ||
            candidate.SdpMid is { Length: > 128 })
        {
            await SendSignalAckAsync(sender, candidate.RequestId, "candidate", false,
                lease is null ? "租约不存在或已过期" : "候选超过限制");
            return;
        }

        if (!candidate.EndOfCandidates)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(candidate.RequestId, out lease) || lease.Authorization.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    lease = null;
                }
                else if (lease.CandidateCount >= MaxCandidatesPerLease)
                {
                    lease = null;
                }
                else
                {
                    _leases[candidate.RequestId] = lease with { CandidateCount = lease.CandidateCount + 1 };
                }
            }
            if (lease is null)
            {
                await SendSignalAckAsync(sender, candidate.RequestId, "candidate", false, "候选数量超过上限或租约已过期");
                return;
            }
        }

        var target = ReferenceEquals(sender, lease.Requester) ? lease.Target : lease.Requester;
        await target.SendPeerSignalAsync(candidate);
        await SendSignalAckAsync(sender, candidate.RequestId, "candidate", true, null);
    }

    private Lease? FindLease(string requestId)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            return _leases.TryGetValue(requestId, out var lease) ? lease : null;
        }
    }

    private void PruneExpiredLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _leases.Where(x => x.Value.Authorization.ExpiresAt <= now).Select(x => x.Key).ToArray())
            _leases.Remove(id);
    }

    private static bool ValidRequest(PeerRequestMessage request, out string error)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > MaxRequestIdLength)
            error = "requestId 无效";
        else if (string.IsNullOrWhiteSpace(request.TargetPeerId))
            error = "目标 Peer 不能为空";
        else if (string.IsNullOrWhiteSpace(request.ServiceId) || request.ServiceId.Length > MaxServiceIdLength)
            error = "serviceId 无效";
        else
        {
            error = "";
            return true;
        }
        return false;
    }

    private static bool ValidSdp(string sdp)
        => !string.IsNullOrWhiteSpace(sdp) && sdp.Length <= MaxSdpLength;

    private static ValueTask RejectRequestAsync(IPeerSignalEndpoint endpoint, string requestId, string error)
        => endpoint.SendPeerSignalAsync(new PeerRequestAckMessage(requestId, false, error));

    private static ValueTask SendSignalAckAsync(IPeerSignalEndpoint endpoint, string requestId, string type, bool ok, string? error)
        => endpoint.SendPeerSignalAsync(new PeerSignalAckMessage(requestId, ok, error, type));
}
