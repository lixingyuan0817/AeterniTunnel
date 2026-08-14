using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Aeterni.Tunnel.Engine.Server;

/// <summary>
/// 内置 Dashboard（FR-042）：监听 dashboardPort，提供 GET /api/status（JSON）。
/// 极简实现：仅处理 /api/status，其余返回 404。
/// </summary>
public sealed class DashboardListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string> _statusJsonProvider;
    private readonly Func<string>? _configJsonProvider;
    private readonly Func<string>? _healthJsonProvider;
    private readonly string _user;
    private readonly string _password;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public DashboardListener(int port, Func<string> statusJsonProvider,
        Func<string>? configJsonProvider = null, Func<string>? healthJsonProvider = null,
        string user = "", string password = "")
    {
        Port = port;
        _statusJsonProvider = statusJsonProvider;
        _configJsonProvider = configJsonProvider;
        _healthJsonProvider = healthJsonProvider;
        _user = user;
        _password = password;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
    }

    public void Start()
    {
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            var path = requestLine?.Split(' ').ElementAtOrDefault(1) ?? "";
            var authHeader = await ReadHeaderAsync(reader, "Authorization");

            // 鉴权：配置了 dashboardUser 则要求 Basic Auth
            if (!Authorize(authHeader))
            {
                var body = "401 Unauthorized";
                var resp = $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"ATS\"\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(resp));
                return;
            }

            var jsonBody = path switch
            {
                "/api/status" => _statusJsonProvider(),
                "/api/config" when _configJsonProvider is not null => _configJsonProvider(),
                "/api/health" when _healthJsonProvider is not null => _healthJsonProvider(),
                _ => null,
            };

            if (jsonBody is not null)
            {
                var resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(jsonBody)}\r\nConnection: close\r\n\r\n{jsonBody}";
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(resp));
            }
            else
            {
                var notFound = "404 Not Found";
                var resp = $"HTTP/1.1 404 Not Found\r\nContent-Length: {notFound.Length}\r\nConnection: close\r\n\r\n{notFound}";
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(resp));
            }
        }
        catch { /* 连接异常 */ }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>读取指定请求头（Basic Auth 用）</summary>
    private static async Task<string?> ReadHeaderAsync(StreamReader reader, string name)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
                break; // 请求头结束
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return line[(name.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Basic Auth 校验；未配置用户则不鉴权</summary>
    private bool Authorize(string? authHeader)
    {
        if (string.IsNullOrEmpty(_user))
            return true;

        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader["Basic ".Length..].Trim()));
            return decoded == $"{_user}:{_password}";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
