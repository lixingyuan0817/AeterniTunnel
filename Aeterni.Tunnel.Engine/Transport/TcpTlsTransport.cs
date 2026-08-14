using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Aeterni.Tunnel.Engine.Transport;

/// <summary>
/// TCP（可选 TLS1.3）传输，默认实现（AD-004）。
/// 客户端侧：ConnectAsync；服务端侧：Server() 创建监听后 AcceptAsync。
/// </summary>
public sealed class TcpTlsTransport : ITunnelTransport, IAsyncDisposable
{
    private readonly TcpListener? _listener;
    private readonly bool _useTls;
    private readonly X509Certificate2? _serverCertificate;
    private readonly string? _tlsTargetHost;
    private readonly bool _validateCertificate;

    public string Name => _useTls ? "tcp+tls" : "tcp";

    private TcpTlsTransport(
        TcpListener? listener,
        bool useTls,
        X509Certificate2? serverCertificate,
        string? tlsTargetHost,
        bool validateCertificate)
    {
        _listener = listener;
        _useTls = useTls;
        _serverCertificate = serverCertificate;
        _tlsTargetHost = tlsTargetHost;
        _validateCertificate = validateCertificate;
    }

    /// <summary>客户端侧工厂：连接 host:port，可选 TLS（targetHost 为 SNI，默认 host）</summary>
    public static TcpTlsTransport Client(string host, int port, bool useTls, string? targetHost = null, bool validateCertificate = true)
    {
        _ = host;
        _ = port;
        return new TcpTlsTransport(null, useTls, null, targetHost, validateCertificate);
    }

    /// <summary>服务端侧工厂：绑定 addr:port 监听；certificate 非空则接受 TLS 连接</summary>
    public static TcpTlsTransport Server(IPAddress bindAddr, int port, X509Certificate2? certificate = null)
    {
        var listener = new TcpListener(bindAddr, port);
        listener.Start();
        return new TcpTlsTransport(listener, certificate is not null, certificate, null, false);
    }

    public async ValueTask<ITunnelConnection> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);

        Stream stream = tcp.GetStream();
        if (_useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = _tlsTargetHost ?? host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            if (!_validateCertificate)
                options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            await ssl.AuthenticateAsClientAsync(options, ct);
            stream = ssl;
        }

        return new TcpConnection(tcp, stream, $"{host}:{port}");
    }

    public async ValueTask<ITunnelConnection> AcceptAsync(CancellationToken ct = default)
    {
        var tcp = await _listener!.AcceptTcpClientAsync(ct);

        Stream stream = tcp.GetStream();
        if (_useTls && _serverCertificate is not null)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCertificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
            stream = ssl;
        }

        var ep = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
        return new TcpConnection(tcp, stream, ep);
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Stop();
        return ValueTask.CompletedTask;
    }
}
