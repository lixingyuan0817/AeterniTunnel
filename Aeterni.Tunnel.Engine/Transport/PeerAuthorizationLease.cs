using System.Security.Cryptography;
using System.Text;

namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// Short-lived capability binding a peer data path to authenticated identities.
/// The token is only an authorization credential; the data-plane adapter must still
/// complete its standard encrypted handshake before delivering business payloads.
/// </summary>
public sealed class PeerAuthorizationLease
{
    public const int MaxLifetimeSeconds = 30;

    private readonly byte[] _tokenBytes;
    private int _revoked;

    private PeerAuthorizationLease(
        string localPeerId,
        string remotePeerId,
        string serviceId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        byte[] tokenBytes)
    {
        LocalPeerId = localPeerId;
        RemotePeerId = remotePeerId;
        ServiceId = serviceId;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        _tokenBytes = tokenBytes;
        Token = Convert.ToHexString(tokenBytes);
    }

    public string LocalPeerId { get; }

    public string RemotePeerId { get; }

    public string ServiceId { get; }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public string Token { get; }

    public bool IsRevoked => Volatile.Read(ref _revoked) != 0;

    public static PeerAuthorizationLease Create(
        string localPeerId,
        string remotePeerId,
        string serviceId,
        DateTimeOffset issuedAt,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePeerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(MaxLifetimeSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime,
                $"Peer authorization leases must be between 1 and {MaxLifetimeSeconds} seconds.");
        }

        return new PeerAuthorizationLease(
            localPeerId,
            remotePeerId,
            serviceId,
            issuedAt,
            issuedAt + lifetime,
            RandomNumberGenerator.GetBytes(32));
    }

    public bool Validate(
        string localPeerId,
        string remotePeerId,
        string serviceId,
        string token,
        DateTimeOffset now)
    {
        if (IsRevoked || now < IssuedAt || now >= ExpiresAt ||
            !string.Equals(localPeerId, LocalPeerId, StringComparison.Ordinal) ||
            !string.Equals(remotePeerId, RemotePeerId, StringComparison.Ordinal) ||
            !string.Equals(serviceId, ServiceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(token);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(_tokenBytes, supplied);
    }

    public void Revoke() => Interlocked.Exchange(ref _revoked, 1);
}
