using Aeterni.Tunnel.Engine.Channels;
using Aeterni.Tunnel.Engine.Protocol;
using Aeterni.Tunnel.Engine.Protocol.Messages;
using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Client;

/// <summary>
/// Agent 会话：连接 Server → Hello 登录 → 注册/注销隧道 → 心跳保活。
/// 断线自动重连（指数退避）并重注册全部隧道（FR-014）。
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (string LocalIp, int LocalPort, LinkType LinkType)> _localProxies = new();
    private readonly Dictionary<string, Traffic.TrafficCounter> _traffic = new();
    private readonly List<(string ProxyId, RegisterProxyMessage Msg)> _desiredProxies = new();
    private ChannelMultiplexer? _mux;
    private int _disposed;
    private int _reconnecting;
    private int _connected;

    public event Action<string>? LogLine;
    public event Action<string, bool, string?>? ProxyRegistered;
    public event Action<string>? ProxyRemoved;

    /// <summary>连接建立（含重连成功）——UI 事件驱动即时响应</summary>
    public event Action? Connected;

    /// <summary>连接断开（断线进入重连 / 停止时触发）</summary>
    public event Action? Disconnected;

    /// <summary>服务端端口策略到达（登录后下发，后台线程）</summary>
    public event Action<PortPolicyMessage>? PortPolicyReceived;

    /// <summary>服务端端口策略（登录后下发；AllowPorts 空 = 不限制）——添加隧道前置校验用</summary>
    public PortPolicyMessage? PortPolicy { get; private set; }

    private TaskCompletionSource<(bool Ok, string? Error, string? Version)>? _pendingHelloAck;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    public AgentSession(AgentOptions options)
    {
        _options = options;
    }

    /// <summary>连接并登录（Hello）；结果经 LogLine / 后续消息体现</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await ConnectCoreAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 初次连接失败（拒绝/超时/登录失败）→ 进入后台重连循环，不阻塞调用方
            LogLine?.Invoke($"连接失败：{ex.Message}，进入后台重连");
            _ = ReconnectLoopAsync();
            throw;
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct = default)
    {
        LogLine?.Invoke($"正在连接 {_options.ServerAddr}:{_options.ServerPort}{(string.IsNullOrEmpty(_options.ClientId) ? "" : $"（{_options.ClientId}）")}…");
        var transport = TcpTlsTransport.Client(
            _options.ServerAddr, _options.ServerPort, _options.UseTls,
            validateCertificate: _options.ValidateCertificate);
        var conn = await transport.ConnectAsync(_options.ServerAddr, _options.ServerPort, ct);

        _mux = new ChannelMultiplexer(conn);
        _mux.ControlHandler = HandleControlAsync;
        _mux.ConnectionClosed += OnConnectionClosed;
        _mux.Start();

        // 握手：发 Hello 并等待服务端 HelloAck（带超时）——连到非 ATS 服务不误判"已连接"
        var ack = new TaskCompletionSource<(bool Ok, string? Error, string? Version)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingHelloAck = ack;
        await SendAsync(new HelloMessage(_options.ClientId, 1, _options.Token, Environment.MachineName));

        var finished = await Task.WhenAny(ack.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
        if (finished != ack.Task)
        {
            LogLine?.Invoke("登录超时（未收到服务端确认，目标可能不是 Aeterni Tunnel 服务）");
            await _mux.DisposeAsync();
            throw new TimeoutException("Hello ack 超时");
        }

        var result = await ack.Task;
        if (!result.Ok)
        {
            LogLine?.Invoke($"登录失败：{result.Error}");
            await _mux.DisposeAsync();
            throw new InvalidOperationException($"登录失败：{result.Error}");
        }

        LogLine?.Invoke($"登录成功 (server {result.Version})");
        Interlocked.Exchange(ref _connected, 1);
        Connected?.Invoke();
        StartHeartbeat();
    }

    public async Task RegisterProxyAsync(
        string proxyId, LinkType linkType, string localIp, int localPort,
        int? remotePort = null, string? domain = null, string? subdomain = null,
        string? group = null, CancellationToken ct = default)
    {
        _localProxies[proxyId] = (localIp, localPort, linkType);
        _traffic[proxyId] = new Traffic.TrafficCounter();
        _desiredProxies.RemoveAll(x => x.ProxyId == proxyId);
        _desiredProxies.Add((proxyId, new RegisterProxyMessage(proxyId, linkType, localIp, localPort, remotePort, domain, subdomain, group)));
        await SendAsync(_desiredProxies[^1].Msg, ct);
    }

    public async Task UnregisterProxyAsync(string proxyId, CancellationToken ct = default)
    {
        _localProxies.Remove(proxyId);
        _traffic.Remove(proxyId);
        _desiredProxies.RemoveAll(x => x.ProxyId == proxyId);
        await SendAsync(new UnregisterProxyMessage(proxyId), ct);
    }

    /// <summary>隧道流量快照（TUI/Dashboard 用）：proxyId → (up, down)</summary>
    public IReadOnlyDictionary<string, (long Up, long Down)> GetTrafficSnapshot()
        => _traffic.ToDictionary(kv => kv.Key, kv => (kv.Value.UpBytes, kv.Value.DownBytes));

    // ═════════ 重连（FR-014） ═════════

    private void OnConnectionClosed()
    {
        Interlocked.Exchange(ref _connected, 0);
        Disconnected?.Invoke();
        LogLine?.Invoke("连接断开，准备重连");
        _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        if (Interlocked.Exchange(ref _reconnecting, 1) != 0)
            return;

        try
        {
            var delay = TimeSpan.FromSeconds(1);
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    await Task.Delay(delay, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await ConnectCoreAsync(_cts.Token);
                    LogLine?.Invoke($"重连成功，重新注册 {_desiredProxies.Count} 个隧道");
                    foreach (var (id, msg) in _desiredProxies)
                    {
                        await SendAsync(msg, _cts.Token);
                        // 不伪造注册成功：等服务端真实 RegisterProxyAck（避免连到非 ATS 显示"在线"假象）
                    }
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                    LogLine?.Invoke($"重连失败，{delay.TotalSeconds:0}s 后重试");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    // ═════════ 控制消息处理 ═════════

    private async ValueTask HandleControlAsync(ushort channelId, byte[] payload)
    {
        var msg = MessageCodec.Deserialize(payload);
        switch (msg)
        {
            case HelloAckMessage ack:
                // 握手结果由 ConnectCoreAsync 统一处理（日志/失败断开/标记已连接）
                _pendingHelloAck?.TrySetResult((ack.Ok, ack.Error, ack.ServerVersion));
                break;

            case PortPolicyMessage policy:
                // 服务端端口策略：allowPorts 白名单 + 每客户端上限（添加隧道前置校验）
                PortPolicy = policy;
                PortPolicyReceived?.Invoke(policy);
                break;

            case RegisterProxyAckMessage ack:
                ProxyRegistered?.Invoke(ack.ProxyId, ack.Ok, ack.RemoteAddr ?? ack.Error);
                break;

            case OpenTunnelMessage open:
                await HandleOpenTunnelAsync(open);
                break;

            case RemoveProxyCommandMessage cmd:
                await HandleRemoveProxyCommandAsync(cmd);
                break;

            case ErrorMessage err:
                LogLine?.Invoke($"服务端错误：{err.Code} {err.Message}");
                break;
        }
    }

    /// <summary>
    /// 服务端指令：删除隧道。本地移除目标（含重连重注册列表，防止僵尸复活），
    /// 通知宿主做本地清理，回 UnregisterProxyMessage 让服务端释放资源（服务端不直接删），
    /// 最后回 CommandAckMessage（幂等：未知隧道也视为成功）。
    /// </summary>
    private async Task HandleRemoveProxyCommandAsync(RemoveProxyCommandMessage cmd)
    {
        _localProxies.Remove(cmd.ProxyId);
        _traffic.Remove(cmd.ProxyId);
        _desiredProxies.RemoveAll(x => x.ProxyId == cmd.ProxyId);
        ProxyRemoved?.Invoke(cmd.ProxyId);
        LogLine?.Invoke($"服务端指令：移除隧道 {cmd.ProxyId}");
        // 通知服务端释放该隧道资源（端口/监听/vhost），走既有注销路径
        await SendAsync(new UnregisterProxyMessage(cmd.ProxyId));
        await SendAsync(new CommandAckMessage("removeProxy", cmd.ProxyId, true, null, cmd.Seq));
    }

    /// <summary>Server 请求建立数据隧道：接受通道 → 连接本地服务 → 双向转发</summary>
    private async Task HandleOpenTunnelAsync(OpenTunnelMessage open)
    {
        if (_mux is null)
            return;
        if (!_localProxies.TryGetValue(open.ProxyId, out var target))
        {
            LogLine?.Invoke($"未知隧道的隧道请求：{open.ProxyId}");
            return;
        }

        var ch = _mux.AcceptChannel(open.ChannelId);
        var counter = _traffic.TryGetValue(open.ProxyId, out var tc) ? tc : null;
        if (target.LinkType == LinkType.Udp)
            _ = UdpTunnelAsync(ch, target, counter);
        else
            _ = ForwardToLocalAsync(ch, target, counter);
    }

    private async Task ForwardToLocalAsync(Channel ch, (string LocalIp, int LocalPort, LinkType LinkType) target, Traffic.TrafficCounter? counter)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            await tcp.ConnectAsync(target.LocalIp, target.LocalPort);
            await TcpBridge.RunAsync(tcp.GetStream(), ch,
                counter is not null ? counter.AddUp : null, counter is not null ? counter.AddDown : null);
        }
        catch { /* 本地连接失败/转发中断，关闭通道 */ }
    }

    /// <summary>UDP 隧道：通道帧 → 本地 UDP；本地 UDP 回复 → 通道帧</summary>
    private async Task UdpTunnelAsync(Channel ch, (string LocalIp, int LocalPort, LinkType LinkType) target, Traffic.TrafficCounter? counter)
    {
        // 必须显式绑定（new UdpClient() 无参构造未绑定，ReceiveAsync 会抛异常）
        using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
        var localEp = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(target.LocalIp), target.LocalPort);

        var toLocal = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var data = await ch.ReadAsync();
                    if (data is null) break;
                    counter?.AddDown(data.Length);
                    await udp.SendAsync(data, data.Length, localEp);
                }
            }
            catch { }
        });

        var toChannel = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync();
                    counter?.AddUp(result.Buffer.Length);
                    await ch.WriteAsync(result.Buffer);
                }
            }
            catch { }
        });

        await Task.WhenAll(toLocal, toChannel);
    }

    // ═════════ 心跳 ═════════

    private void StartHeartbeat()
    {
        var interval = _options.HeartbeatInterval == default
            ? TimeSpan.FromSeconds(10)
            : _options.HeartbeatInterval;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(_cts.Token))
                    await SendAsync(new HeartbeatMessage(Environment.TickCount64));
            }
            catch (OperationCanceledException) { }
            catch { /* 连接断开，心跳停止 */ }
        }, _cts.Token);
    }

    private async ValueTask SendAsync(Message msg, CancellationToken ct = default)
    {
        if (_mux is null)
            throw new InvalidOperationException("Agent 尚未连接");
        await _mux.SendControlAsync(MessageCodec.Serialize(msg), ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cts.Cancel();
        if (_mux is not null)
            await _mux.DisposeAsync();
        Disconnected?.Invoke();
    }
}
