using Aeterni.Tunnel.Engine.Hosting;
using Aeterni.Tunnel.Engine.Transport;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aeterni.Tunnel.Engine.Tests;

public sealed class TlsPolicyTests
{
    [Fact]
    public void ServerHost_RejectsTlsWithoutCertificate()
    {
        var host = new ServerHost();

        var error = Assert.Throws<InvalidOperationException>(() =>
            host.Start(new ServerHostOptions(0, "test-token")));

        Assert.Contains("TlsCertificatePath", error.Message);
    }

    [Fact]
    public async Task ServerHost_RequiresExplicitOptInForPlaintext()
    {
        var host = new ServerHost();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                host.Start(new ServerHostOptions(0, "test-token", UseTls: false)));
            Assert.Contains("AllowInsecureTransport", error.Message);

            host.Start(new ServerHostOptions(0, "test-token", UseTls: false, AllowInsecureTransport: true));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ServerHost_LoadsConfiguredPkcs12Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var raw = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var path = Path.Combine(Path.GetTempPath(), "aeterni-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, raw.Export(X509ContentType.Pkcs12, "test-password"));

        var host = new ServerHost();
        try
        {
            host.Start(new ServerHostOptions(
                0,
                "test-token",
                TlsCertificatePath: path,
                TlsCertificatePassword: "test-password"));

            Assert.NotNull(host.Listener);
        }
        finally
        {
            await host.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TlsClient_UsesConfiguredRootAndServerName()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var raw = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pkcs12, "pwd"), "pwd");
        var caPath = Path.Combine(Path.GetTempPath(), "aeterni-ca-" + Guid.NewGuid().ToString("N") + ".cer");
        await File.WriteAllBytesAsync(caPath, raw.Export(X509ContentType.Cert));
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0, serverCertificate);
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls: true,
            targetHost: "localhost", validateCertificate: true, caCertificatePath: caPath);

        try
        {
            var accept = server.AcceptAsync().AsTask();
            await using var clientConnection = await client.ConnectAsync("127.0.0.1", port);
            await using var serverConnection = await accept;
            Assert.Equal("tcp+tls", client.Name);
        }
        finally
        {
            File.Delete(caPath);
        }
    }

    [Fact]
    public async Task TlsClient_RejectsUntrustedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0,
            X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null));
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls: true, targetHost: "localhost");
        var accept = server.AcceptAsync().AsTask();

        await Assert.ThrowsAnyAsync<Exception>(async () => await client.ConnectAsync("127.0.0.1", port));
        try { await accept; } catch { }
    }

    [Fact]
    public async Task TlsClient_RejectsServerNameMismatch()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var raw = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(raw.Export(X509ContentType.Pkcs12), null);
        var caPath = Path.Combine(Path.GetTempPath(), "aeterni-ca-" + Guid.NewGuid().ToString("N") + ".cer");
        await File.WriteAllBytesAsync(caPath, raw.Export(X509ContentType.Cert));
        await using var server = TcpTlsTransport.Server(IPAddress.Loopback, 0, serverCertificate);
        var port = GetListeningPort(server);
        await using var client = TcpTlsTransport.Client("127.0.0.1", port, useTls: true,
            targetHost: "wrong.example", validateCertificate: true, caCertificatePath: caPath);
        var accept = server.AcceptAsync().AsTask();

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await client.ConnectAsync("127.0.0.1", port));
            try { await accept; } catch { }
        }
        finally
        {
            File.Delete(caPath);
        }
    }

    private static int GetListeningPort(TcpTlsTransport transport)
    {
        var listener = typeof(TcpTlsTransport).GetField("_listener", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(transport) as System.Net.Sockets.TcpListener;
        return ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? throw new InvalidOperationException("Could not resolve test listener port.");
    }
}
