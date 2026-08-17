namespace Aeterni.Tunnel.Launcher.Models;

/// <summary>实例运行状态</summary>
public enum InstanceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
    Error,
}

/// <summary>进程输出行（控制台管道）</summary>
public record ConsoleLine(DateTime Time, string Text, bool IsStderr);

/// <summary>实例状态变化事件参数</summary>
public sealed class InstanceStateChangedEventArgs(InstanceState state, string? message = null) : EventArgs
{
    public InstanceState State { get; } = state;
    public string? Message { get; } = message;
}
