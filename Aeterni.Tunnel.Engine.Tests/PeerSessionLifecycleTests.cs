using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class PeerSessionLifecycleTests
{
    [Fact]
    public async Task CloseIsIdempotentAndPublishesTerminalState()
    {
        var session = new PeerSessionLifecycle("peer-a", "peer-b");
        var states = new List<CommunicationSessionState>();
        session.StateChanged += states.Add;

        session.BeginConnect();
        session.MarkConnected();
        await session.CloseAsync("test");
        await session.CloseAsync("duplicate");

        Assert.Equal(CommunicationSessionState.Closed, session.State);
        Assert.Equal([CommunicationSessionState.Connecting, CommunicationSessionState.Connected,
            CommunicationSessionState.Closing, CommunicationSessionState.Closed], states);
    }

    [Fact]
    public async Task QualityUpdatesAreIgnoredAfterClose()
    {
        var session = new PeerSessionLifecycle("peer-a", "peer-b");
        var updates = 0;
        session.QualityChanged += _ => updates++;
        session.BeginConnect();
        session.MarkConnected();
        var quality = new PeerQualitySnapshot(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(12), 10, 20, 1);
        session.UpdateQuality(quality);

        await session.CloseAsync("test");
        session.UpdateQuality(quality with { SentBytes = 30 });

        Assert.Equal(1, updates);
        Assert.Equal(quality, session.Quality);
    }

    [Fact]
    public async Task ConcurrentCloseLeavesSessionClosed()
    {
        var session = new PeerSessionLifecycle("peer-a", "peer-b");
        session.BeginConnect();
        session.MarkConnected();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.CloseAsync("race").AsTask()));

        Assert.Equal(CommunicationSessionState.Closed, session.State);
    }

    [Fact]
    public async Task ExpiredLeaseClosesPeerWithoutReconnect()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(2));
        var session = new PeerSessionLifecycle("peer-a", "peer-b", lease: lease);
        session.BeginConnect();
        session.MarkConnected();

        Assert.False(session.EnforceLease(lease.ExpiresAt));
        await Task.Yield();
        Assert.Equal(CommunicationSessionState.Closed, session.State);
    }

    [Fact]
    public async Task RevokedLeaseClosesPeer()
    {
        var issued = DateTimeOffset.UtcNow;
        var lease = PeerAuthorizationLease.Create("peer-a", "peer-b", "svc", issued, TimeSpan.FromSeconds(10));
        var session = new PeerSessionLifecycle("peer-a", "peer-b", lease: lease);
        session.BeginConnect();
        session.MarkConnected();
        lease.Revoke();

        Assert.False(session.EnforceLease(issued.AddSeconds(1)));
        await Task.Yield();
        Assert.Equal(CommunicationSessionState.Closed, session.State);
    }
}
