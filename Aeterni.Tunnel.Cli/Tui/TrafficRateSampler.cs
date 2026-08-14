namespace Aeterni.Tunnel.Cli.Tui;

/// <summary>
/// 流量速率采样器：基于累计字节快照差值 + 指数平滑（EMA），
/// 供 TUI 每秒显示 ↑上行/↓下行速率。
/// </summary>
public sealed class TrafficRateSampler
{
    private readonly Dictionary<string, (long Up, long Down, long Tick)> _last = new();
    private readonly Dictionary<string, (double Up, double Down)> _ema = new();

    /// <summary>采样：返回平滑后的 (上行, 下行) 速率（bytes/s）</summary>
    public (double Up, double Down) Sample(string proxyId, long upBytes, long downBytes)
    {
        var now = Environment.TickCount64;
        (double Up, double Down) rate = (0, 0);

        if (_last.TryGetValue(proxyId, out var last))
        {
            var elapsed = (now - last.Tick) / 1000.0;
            if (elapsed > 0)
            {
                var up = Math.Max(0, (upBytes - last.Up) / elapsed);
                var down = Math.Max(0, (downBytes - last.Down) / elapsed);
                rate = (up, down);
            }
        }
        _last[proxyId] = (upBytes, downBytes, now);

        // EMA 平滑（alpha=0.5），避免 500ms 采样抖动
        if (_ema.TryGetValue(proxyId, out var prev))
            rate = (prev.Up * 0.5 + rate.Up * 0.5, prev.Down * 0.5 + rate.Down * 0.5);
        _ema[proxyId] = rate;
        return rate;
    }
}
