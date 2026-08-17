using Avalonia.Media;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>开服页实例卡片（示例界面；后续接入 Aeterni.Tunnel.Launcher 真实状态）</summary>
public sealed class ServerCardViewModel
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
    public string Java { get; set; } = "";
    public string Port { get; set; } = "";
    public string Stats { get; set; } = "";
    public bool IsRunning { get; set; }

    public string StateText => IsRunning ? "● 运行中" : "○ 已停止";
    public IBrush StateBrush => IsRunning
        ? new SolidColorBrush(Color.Parse("#34C759"))
        : new SolidColorBrush(Color.Parse("#8E8E93"));
    public bool IsStopped => !IsRunning;
    public string Meta => $"{Java} · {Port}{(Stats.Length > 0 ? " · " + Stats : "")}";
}
