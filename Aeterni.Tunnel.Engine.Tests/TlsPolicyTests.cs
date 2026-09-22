using Aeterni.Tunnel.Engine.Hosting;
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
}
