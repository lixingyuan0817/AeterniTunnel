using System.Net;
using Aeterni.Tunnel.Engine.Server;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class UdpSourceRegistryTests
{
    [Fact]
    public void SameSource_ReusesAssociation()
    {
        var registry = new UdpSourceRegistry(4, TimeSpan.FromMinutes(1));
        var endpoint = new IPEndPoint(IPAddress.Loopback, 41001);

        var first = registry.GetOrCreate(endpoint);
        var second = registry.GetOrCreate(endpoint);

        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(first, out var resolved));
        Assert.Equal(endpoint, resolved);
    }

    [Fact]
    public void Capacity_EvictsLeastRecentlySeenSource()
    {
        var clock = new ManualTimeProvider();
        var registry = new UdpSourceRegistry(2, TimeSpan.FromMinutes(10), clock);
        var first = registry.GetOrCreate(new IPEndPoint(IPAddress.Loopback, 41001));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = registry.GetOrCreate(new IPEndPoint(IPAddress.Loopback, 41002));
        clock.Advance(TimeSpan.FromSeconds(1));
        var third = registry.GetOrCreate(new IPEndPoint(IPAddress.Loopback, 41003));

        Assert.False(registry.TryGet(first, out _));
        Assert.True(registry.TryGet(second, out _));
        Assert.True(registry.TryGet(third, out _));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void IdleTimeout_RemovesAssociationAndAllocatesNewId()
    {
        var clock = new ManualTimeProvider();
        var registry = new UdpSourceRegistry(4, TimeSpan.FromSeconds(30), clock);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 41001);
        var expiredId = registry.GetOrCreate(endpoint);

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(registry.TryGet(expiredId, out _));
        Assert.Equal(0, registry.Count);
        Assert.NotEqual(expiredId, registry.GetOrCreate(endpoint));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
