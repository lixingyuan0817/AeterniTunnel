using System.Text.Json;

namespace Aeterni.Tunnel.Engine.Protocol.Messages;

/// <summary>
/// 控制消息 JSON 编解码（NativeAOT 兼容：基于源生成上下文，无反射）。
/// 基类 Message 用 JsonPolymorphic 携带 "type" 判别字段，反序列化按 type 还原具体消息。
/// </summary>
public static class MessageCodec
{
    public static byte[] Serialize(Message message)
        => JsonSerializer.SerializeToUtf8Bytes(message, MessageJsonContext.Default.Message);

    public static Message? Deserialize(byte[] payload)
        => JsonSerializer.Deserialize(payload, MessageJsonContext.Default.Message);

    public static string SerializeToString(Message message)
        => JsonSerializer.Serialize(message, MessageJsonContext.Default.Message);
}
