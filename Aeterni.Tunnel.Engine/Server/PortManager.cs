using System.Threading.Channels;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 远程端口分配/释放（FR-040/FR-041）。支持指定端口与随机分配；端口冲突报错；
/// 配置 allowPorts 白名单后仅允许范围内的端口。
/// </summary>
public sealed class PortManager
{
    private readonly HashSet<int> _allocated = new();
    private readonly object _lock = new();
    private readonly Random _random = new();
    private readonly IReadOnlyList<PortRange>? _allowed;
    private readonly int _randomMin;
    private readonly int _randomMax;

    public PortManager(int randomMin = 20000, int randomMax = 60000, IReadOnlyList<PortRange>? allowed = null)
    {
        _randomMin = randomMin;
        _randomMax = randomMax;
        _allowed = allowed;
    }

    /// <summary>分配端口；requested 为 null/≤0 时随机分配。越界或冲突抛 InvalidOperationException。</summary>
    public int Allocate(int? requested)
    {
        lock (_lock)
        {
            if (requested is > 0)
            {
                EnsureAllowed(requested.Value);
                if (_allocated.Contains(requested.Value))
                    throw new InvalidOperationException($"端口 {requested} 已被占用");
                _allocated.Add(requested.Value);
                return requested.Value;
            }

            for (var i = 0; i < 200; i++)
            {
                var p = _random.Next(_randomMin, _randomMax);
                if (IsAllowed(p) && !_allocated.Contains(p))
                {
                    _allocated.Add(p);
                    return p;
                }
            }

            throw new InvalidOperationException("无可用端口");
        }
    }

    /// <summary>释放端口（幂等）</summary>
    public void Release(int port)
    {
        lock (_lock) _allocated.Remove(port);
    }

    /// <summary>白名单端口数量（Dashboard 展示用；0 = 不限制）</summary>
    public int GetAllowedCount()
        => _allowed?.Count ?? 0;

    public bool IsAllocated(int port)
    {
        lock (_lock) return _allocated.Contains(port);
    }

    private void EnsureAllowed(int port)
    {
        if (!IsAllowed(port))
            throw new InvalidOperationException($"端口 {port} 不在允许范围（allowPorts）");
    }

    private bool IsAllowed(int port)
    {
        if (_allowed is null || _allowed.Count == 0)
            return true;
        return _allowed.Any(r => port >= r.Start && port <= r.End);
    }
}
