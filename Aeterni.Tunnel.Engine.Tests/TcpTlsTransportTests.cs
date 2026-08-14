using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aeterni.Tunnel.Engine.Transport;

namespace Aeterni.Tunnel.Engine.Tests;

public class TcpTlsTransportTests
{
    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var raw = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // 导出为 PFX 再重新加载，确保私钥在 Windows 上干净绑定到证书
        const string pwd = "aeterni-test";
        var pfx = raw.Export(X509ContentType.Pkcs12, pwd);
        return X509CertificateLoader.LoadPkcs12(pfx, pwd);
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void SelfSignedCert_Creates()
    {
        using var cert = CreateSelfSignedCert();
        Assert.NotNull(cert);
        Assert.Equal("CN=localhost", cert.Subject);
    }

    [Fact]
    public async Task PlainTcp_EchoRoundTrip()
    {
        var port = FreePort();
        var server = TcpTlsTransport.Server(IPAddress.Loopback, port);
        var client = TcpTlsTransport.Client("127.0.0.1", port, useTls: false);

        var payload = Encoding.UTF8.GetBytes("hello-tunnel");

        var acceptTask = Task.Run(async () =>
        {
            await using var conn = await server.AcceptAsync();
            var buf = new byte[payload.Length];
            await conn.Stream.ReadExactlyAsync(buf);
            await conn.Stream.WriteAsync(buf);
        });

        await using var clientConn = await client.ConnectAsync("127.0.0.1", port);
        await clientConn.Stream.WriteAsync(payload);
        var echo = new byte[payload.Length];
        await clientConn.Stream.ReadExactlyAsync(echo);

        Assert.Equal(payload, echo);
        Assert.Equal("tcp", client.Name);
        await server.DisposeAsync();
        await acceptTask;
    }

    [Fact]
    public async Task Tls13_EchoRoundTrip_WithSelfSignedCert()
    {
        var port = FreePort();
        using var cert = CreateSelfSignedCert();
        var server = TcpTlsTransport.Server(IPAddress.Loopback, port, cert);
        var client = TcpTlsTransport.Client("127.0.0.1", port, useTls: true, targetHost: "localhost", validateCertificate: false);

        var payload = Encoding.UTF8.GetBytes("tls-encrypted-data");

        var acceptTask = Task.Run(async () =>
        {
            await using var conn = await server.AcceptAsync();
            var buf = new byte[payload.Length];
            await conn.Stream.ReadExactlyAsync(buf);
            await conn.Stream.WriteAsync(buf);
        });

        await using var clientConn = await client.ConnectAsync("127.0.0.1", port);
        await clientConn.Stream.WriteAsync(payload);
        var echo = new byte[payload.Length];
        await clientConn.Stream.ReadExactlyAsync(echo);

        Assert.Equal(payload, echo);
        Assert.Equal("tcp+tls", client.Name);
        Assert.StartsWith("127.0.0.1:", clientConn.RemoteEndPoint);
        await server.DisposeAsync();
        await acceptTask;
    }
}
