using Aeterni.Tunnel.Engine.Traffic;

namespace Aeterni.Tunnel.Engine.Tests;

public class TrafficCounterTests
{
    [Fact]
    public void AddUpDown_Accumulates()
    {
        var c = new TrafficCounter();
        c.AddUp(100);
        c.AddUp(50);
        c.AddDown(200);

        Assert.Equal(150, c.UpBytes);
        Assert.Equal(200, c.DownBytes);
    }

    [Fact]
    public void ConcurrentAdds_AreConsistent()
    {
        var c = new TrafficCounter();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                c.AddUp(1);
                c.AddDown(1);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        Assert.Equal(8000, c.UpBytes);
        Assert.Equal(8000, c.DownBytes);
    }
}
