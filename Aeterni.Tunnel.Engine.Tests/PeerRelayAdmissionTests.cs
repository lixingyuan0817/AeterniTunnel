using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class PeerRelayAdmissionTests
{
    [Fact]
    public void RelayRequiresLeaseAuthenticationAndBoundsResources()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var admission = new PeerRelayAdmission(lease, new PeerRelayLimits
        {
            MaxPendingPackets = 2,
            MaxPendingBytes = 10,
            MaxPacketBytes = 8,
        });

        Assert.False(admission.TryReserve(1, out _));
        Assert.True(admission.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.True(admission.TryReserve(6, out var first));
        Assert.False(admission.TryReserve(5, out _));
        Assert.True(admission.TryReserve(4, out var second));
        Assert.False(admission.TryReserve(1, out _));
        Assert.Equal((2, 10), admission.Pending);

        first!.Dispose();
        second!.Dispose();
        Assert.Equal((0, 0), admission.Pending);
    }

    [Fact]
    public void RelayRejectsOversizedPacketsAndClosedSessions()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var admission = new PeerRelayAdmission(lease, new PeerRelayLimits { MaxPacketBytes = 4, MaxPendingBytes = 8 });
        Assert.True(admission.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.False(admission.TryReserve(5, out _));

        admission.Close();
        Assert.False(admission.TryReserve(1, out _));
        Assert.False(admission.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(2)));
    }

    [Fact]
    public void ReservationReleaseIsIdempotent()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var admission = new PeerRelayAdmission(lease);
        Assert.True(admission.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.True(admission.TryReserve(10, out var reservation));

        reservation!.Dispose();
        reservation.Dispose();
        Assert.Equal((0, 0), admission.Pending);
    }
}
