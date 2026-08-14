using System.Text;

namespace Aeterni.Tunnel.Engine.Protocol;

/// <summary>
/// TLS ClientHello SNI 解析（FR-033）：从 ClientHello 二进制中提取 server_name。
/// 仅做解析不握手，用于 vhost HTTPS 按域名路由（TLS 流量透传，不终结）。
/// 结构参考 RFC 5246/6066：
///   ClientHello = handshakeType(1)=0x01 + handshakeLen(3) + version(2) + random(32)
///               + sessionIdLen(1)+sessionId + cipherLen(2)+ciphers + compLen(1)+comp
///               + extensionsLen(2) + extensions
///   server_name ext type=0x0000：nameListLen(2)+nameType(1)=0+nameLen(2)+name
/// </summary>
public static class SniParser
{
    public static string? ParseClientHello(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        if (data.Length < 4 || data[pos] != 0x01)
            return null;

        var handshakeLen = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
        pos += 4;
        if (pos + handshakeLen > data.Length)
            return null;

        // version(2) + random(32)
        pos += 34;
        if (pos >= data.Length)
            return null;

        var sessionIdLen = data[pos];
        pos += 1 + sessionIdLen;
        if (pos + 2 > data.Length)
            return null;

        var cipherLen = (data[pos] << 8) | data[pos + 1];
        pos += 2 + cipherLen;
        if (pos >= data.Length)
            return null;

        var compressionLen = data[pos];
        pos += 1 + compressionLen;
        if (pos + 2 > data.Length)
            return null;

        var extensionsLen = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        var extEnd = Math.Min(pos + extensionsLen, data.Length);

        while (pos + 4 <= extEnd)
        {
            var extType = (data[pos] << 8) | data[pos + 1];
            var extDataLen = (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            if (pos + extDataLen > extEnd)
                break;
            if (extType == 0x0000)
                return ParseServerName(data.Slice(pos, extDataLen));
            pos += extDataLen;
        }

        return null;
    }

    private static string? ParseServerName(ReadOnlySpan<byte> ext)
    {
        if (ext.Length < 2)
            return null;
        var listLen = (ext[0] << 8) | ext[1];
        var pos = 2;
        if (pos + listLen > ext.Length)
            return null;
        if (pos >= ext.Length || ext[pos] != 0)
            return null; // 仅支持 host_name

        pos += 1;
        if (pos + 2 > ext.Length)
            return null;
        var nameLen = (ext[pos] << 8) | ext[pos + 1];
        pos += 2;
        if (pos + nameLen > ext.Length)
            return null;

        return Encoding.ASCII.GetString(ext.Slice(pos, nameLen));
    }
}
