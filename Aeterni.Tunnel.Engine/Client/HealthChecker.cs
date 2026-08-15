using System.Net.Sockets;

namespace Aeterni.Tunnel.Engine.Client;

/// <summary>
/// 健康检查器（frp healthCheck 语义）：周期探测本地服务（tcp 连接 / http GET）。
/// 连续失败 MaxFailed 次 → IsHealthy=false 并触发 StatusChanged(false)（调用方摘除隧道）；
/// 恢复后触发 StatusChanged(true)（重新上线）。
/// </summary>
public sealed class HealthChecker : IHealthChecker
{
    private readonly string _host;
    private readonly int _port;
    private readonly HealthCheckOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new();
    private int _failCount;
    private bool _healthy = true;

    /// <summary>健康状态变化：true=恢复 / false=判定不健康</summary>
    public event Action<bool>? StatusChanged;

    public bool IsHealthy => _healthy;

    public HealthChecker(string host, int port, HealthCheckOptions options)
    {
        _host = host;
        _port = port;
        _options = options;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    }

    public void Start()
    {
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        try
        {
            while (true)
            {
                await Task.Delay(interval, _cts.Token);
                var ok = await ProbeAsync();

                if (ok)
                {
                    _failCount = 0;
                    if (!_healthy)
                    {
                        _healthy = true;
                        StatusChanged?.Invoke(true);
                    }
                }
                else
                {
                    _failCount++;
                    if (_failCount >= Math.Max(1, _options.MaxFailed) && _healthy)
                    {
                        _healthy = false;
                        StatusChanged?.Invoke(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            if (_options.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                var url = $"http://{_host}:{_port}{_options.Path ?? "/"}";
                using var resp = await _http.GetAsync(url, _cts.Token);
                return resp.IsSuccessStatusCode;
            }

            // tcp：连接成功即健康（显式检查异步任务结果，避免 Connected 过期状态误判）
            using var client = new TcpClient();
            var connect = client.ConnectAsync(_host, _port);
            var timeout = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)), _cts.Token);
            var completed = await Task.WhenAny(connect, timeout);
            if (completed != connect)
                return false; // 超时
            if (connect.IsFaulted || connect.IsCanceled)
                return false; // 连接被拒/失败
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
