using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class PeerDataPlaneGateTests
{
    [Fact]
    public void BusinessPayloadIsBlockedUntilLeaseAuthentication()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var gate = new PeerDataPlaneGate(lease);
        var delivered = 0;

        Assert.False(gate.TryDeliver(new byte[] { 1 }, _ => delivered++));
        Assert.Equal(0, delivered);
        Assert.True(gate.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.True(gate.TryDeliver(new byte[] { 2 }, _ => delivered++));
        Assert.Equal(1, delivered);
        Assert.Equal(PeerDataPlaneState.Authenticated, gate.State);
    }

    [Fact]
    public void WrongOrReplayedAuthenticationCannotOpenGate()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var gate = new PeerDataPlaneGate(lease);

        Assert.False(gate.Authenticate("peer-x", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.False(gate.Authenticate("peer-a", "peer-b", "svc", "00", issued.AddSeconds(1)));
        Assert.True(gate.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.False(gate.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(2)));
    }

    [Fact]
    public void CloseStopsDeliveryAndAuthentication()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var gate = new PeerDataPlaneGate(lease);
        gate.Close();

        Assert.Equal(PeerDataPlaneState.Closed, gate.State);
        Assert.False(gate.Authenticate("peer-a", "peer-b", "svc", lease.Token, issued.AddSeconds(1)));
        Assert.False(gate.TryDeliver(ReadOnlyMemory<byte>.Empty, _ => { }));
    }
}
