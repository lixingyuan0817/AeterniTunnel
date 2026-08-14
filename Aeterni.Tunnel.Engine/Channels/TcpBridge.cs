using System.Net.Sockets;

namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>
/// TCP 隧道双向转发（FR-020/FR-022）：
/// - local EOF → 半关闭本地发送 + 发 Close 帧通知对端；
/// - channel EOF（对端 Close）→ 半关闭本地发送，等待对端读完剩余数据。
/// </summary>
public static class TcpBridge
{
    private const int BufferSize = 8192;

    /// <summary>
    /// 在本地流与通道之间双向转发，直到任一端 EOF。
    /// onLocalToChannel / onChannelToLocal 为可选的流量统计回调（按字节数）。
    /// </summary>
    public static async Task RunAsync(
        Stream localStream, Channel channel,
        Action<int>? onLocalToChannel = null, Action<int>? onChannelToLocal = null,
        CancellationToken ct = default)
    {
        var toChannel = CopyLocalToChannelAsync(localStream, channel, onLocalToChannel, ct);
        var toLocal = CopyChannelToLocalAsync(channel, localStream, onChannelToLocal, ct);
        await Task.WhenAll(toChannel, toLocal);
    }

    private static async Task CopyLocalToChannelAsync(Stream local, Channel channel, Action<int>? onLocalToChannel, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var n = await local.ReadAsync(buffer, ct);
                if (n == 0)
                    break; // local EOF
                onLocalToChannel?.Invoke(n);
                await channel.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 连接异常，走 finally 通知关闭 */ }
        finally
        {
            TryShutdownSend(local);          // 半关闭：本地不再写
            await channel.CloseAsync();      // 通知对端本方向结束
        }
    }

    private static async Task CopyChannelToLocalAsync(Channel channel, Stream local, Action<int>? onChannelToLocal, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var data = await channel.ReadAsync(ct);
                if (data is null)
                    break; // 对端已关闭
                onChannelToLocal?.Invoke(data.Length);
                await local.WriteAsync(data, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            TryShutdownSend(local);          // 对端不再发数据：本地半关闭发送
            await local.FlushAsync(ct);
            local.Dispose();
        }
    }

    /// <summary>尽力半关闭发送方向（仅 NetworkStream 可 Shutdown；TLS 流不做）</summary>
    private static void TryShutdownSend(Stream stream)
    {
        if (stream is NetworkStream ns)
        {
            try
            {
                ns.Socket.Shutdown(SocketShutdown.Send);
            }
            catch { /* 已关闭或非 TCP */ }
        }
    }
}
