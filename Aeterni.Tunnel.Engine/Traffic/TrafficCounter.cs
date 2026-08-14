namespace Aeterni.Tunnel.Engine.Traffic;

/// <summary>
/// 代理流量计数器（原子，线程安全）。
/// 方向语义：Up = 本地→通道（发送到远端）；Down = 通道→本地（从远端接收）。
/// 同一隧道两端独立统计（Agent 的 Up 对应 Server 的 Down，反之亦然）。
/// </summary>
public sealed class TrafficCounter
{
    private long _upBytes;
    private long _downBytes;

    public long UpBytes => Interlocked.Read(ref _upBytes);
    public long DownBytes => Interlocked.Read(ref _downBytes);

    public void AddUp(int bytes) => Interlocked.Add(ref _upBytes, bytes);
    public void AddDown(int bytes) => Interlocked.Add(ref _downBytes, bytes);
}
