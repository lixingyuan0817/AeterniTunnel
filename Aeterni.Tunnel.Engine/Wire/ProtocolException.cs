namespace Aeterni.Tunnel.Engine.Wire;

/// <summary>
/// 协议层异常：Magic 不符、版本不符、帧超长等。
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}
