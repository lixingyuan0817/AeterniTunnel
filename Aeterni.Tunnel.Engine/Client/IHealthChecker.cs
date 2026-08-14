namespace Aeterni.Tunnel.Engine.Client;

/// <summary>
/// 健康检查器抽象（AgentHost 依赖注入用；测试可注入 fake）。
/// </summary>
public interface IHealthChecker : IAsyncDisposable
{
    /// <summary>健康状态变化：true=恢复 / false=判定不健康</summary>
    event Action<bool>? StatusChanged;

    bool IsHealthy { get; }

    /// <summary>开始周期探测</summary>
    void Start();
}
