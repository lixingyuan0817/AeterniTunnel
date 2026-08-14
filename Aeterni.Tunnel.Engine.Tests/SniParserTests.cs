using System.Text;
using Aeterni.Tunnel.Engine.Protocol;

namespace Aeterni.Tunnel.Engine.Tests;

public class SniParserTests
{
    /// <summary>构造一个含指定 SNI 的最小 ClientHello</summary>
    private static byte[] BuildClientHello(string serverName)
    {
        var name = Encoding.ASCII.GetBytes(serverName);

        // server_name extension: type(2)=0x0000 + len(2) + data
        var extData = new byte[2 + 1 + 2 + name.Length];
        extData[0] = (byte)((1 + 2 + name.Length) >> 8);
        extData[1] = (byte)(1 + 2 + name.Length);   // name_list 长度
        extData[2] = 0;                              // name_type host_name
        extData[3] = (byte)(name.Length >> 8);
        extData[4] = (byte)name.Length;
        name.CopyTo(extData, 5);

        var ext = new byte[4 + extData.Length];
        ext[0] = 0x00; ext[1] = 0x00;                // type server_name
        ext[2] = (byte)(extData.Length >> 8);
        ext[3] = (byte)extData.Length;
        extData.CopyTo(ext, 4);

        // ClientHello 主体（无 sessionId、无 cipher/compression 前的内容尽量精简）
        // handshakeType(1) + len(3) + version(2) + random(32)
        var body = new byte[44 + ext.Length];
        body[0] = 0x01;                              // handshakeType client_hello
        var helloLen = 2 + 32 + 1 + 2 + 1 + 2 + ext.Length; // 不含 type+len 4 字节
        body[1] = (byte)(helloLen >> 16);
        body[2] = (byte)(helloLen >> 8);
        body[3] = (byte)helloLen;
        body[4] = 0x03; body[5] = 0x03;              // TLS 1.2
        // random 32 字节（全 0）
        var offset = 6 + 32;
        body[offset] = 0;                            // sessionIdLen=0
        offset += 1;
        body[offset] = 0; body[offset + 1] = 0;      // cipherSuitesLen=0
        offset += 2;
        body[offset] = 0;                            // compressionLen=0
        offset += 1;
        body[offset] = (byte)(ext.Length >> 8);
        body[offset + 1] = (byte)ext.Length;
        ext.CopyTo(body, offset + 2);

        return body;
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("web.example.com")]
    [InlineData("sub.domain.org")]
    public void Parse_ExtractsServerName(string sni)
    {
        var hello = BuildClientHello(sni);
        Assert.Equal(sni, SniParser.ParseClientHello(hello));
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        Assert.Null(SniParser.ParseClientHello(ReadOnlySpan<byte>.Empty));
        Assert.Null(SniParser.ParseClientHello(new byte[] { 0x01, 0, 0 }));
    }

    [Fact]
    public void Parse_NonClientHelloType_ReturnsNull()
    {
        var hello = BuildClientHello("example.com");
        hello[0] = 0x02; // 非 client_hello
        Assert.Null(SniParser.ParseClientHello(hello));
    }

    [Fact]
    public void Parse_NoSniExtension_ReturnsNull()
    {
        var hello = BuildClientHello("example.com");
        // 扩展从偏移 44 开始：type(2)=0x0000，改为 0x000d(signature_algorithms)
        hello[44] = 0x00;
        hello[45] = 0x0d;
        Assert.Null(SniParser.ParseClientHello(hello));
    }
}
