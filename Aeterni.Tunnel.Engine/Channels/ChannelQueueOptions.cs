namespace Aeterni.Tunnel.Engine.Channels;

/// <summary>每条可靠通道的收发队列预算；同时限制包数和负载字节数。</summary>
public sealed record ChannelQueueOptions(
    int MaxQueuedPackets = 64,
    long MaxQueuedBytes = 8 * 1024 * 1024)
{
    internal void Validate()
    {
        if (MaxQueuedPackets <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedPackets));
        if (MaxQueuedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedBytes));
    }
}
