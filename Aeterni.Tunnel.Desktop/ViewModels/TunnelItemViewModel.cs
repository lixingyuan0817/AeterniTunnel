using System.Windows.Input;
using Aeterni.Tunnel.Engine.Hosting;
using Avalonia.Media;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>隧道状态（驱动颜色与文案）</summary>
public enum TunnelUiState
{
    Registered,   // 已注册 → 运行中（绿）
    Pending,      // 注册中（黄）
    Failed,       // 注册失败（红）
    Offline,      // 未连接 → 离线（灰）
}

/// <summary>隧道列表行 VM（名称/类型徽章/本地→远程/状态/流量/删除）</summary>
public sealed class TunnelItemViewModel : ObservableBase
{
    private static readonly IBrush Green = SolidColorBrush.Parse("#34C759");
    private static readonly IBrush Yellow = SolidColorBrush.Parse("#FBBF24");
    private static readonly IBrush Red = SolidColorBrush.Parse("#F87171");
    private static readonly IBrush Gray = SolidColorBrush.Parse("#64748B");
    private static readonly IBrush Teal = SolidColorBrush.Parse("#5AC8FA");
    private static readonly IBrush Purple = SolidColorBrush.Parse("#A78BFA");
    private static readonly IBrush Blue = SolidColorBrush.Parse("#60A5FA");

    private string _remote = "";
    private TunnelUiState _state = TunnelUiState.Offline;
    private long _up;
    private long _down;

    public string ProxyId { get; }

    public string Type { get; }

    public string Local { get; }

    public TunnelItemViewModel(ProxyDefinition def, ICommand removeCommand, ICommand editCommand)
    {
        ProxyId = def.ProxyId;
        Type = def.LinkType.ToString().ToLowerInvariant();
        Local = $"{def.LocalIp}:{def.LocalPort}";
        GroupName = string.IsNullOrWhiteSpace(def.Group) ? "default" : def.Group;
        RemoveCommand = removeCommand;
        EditCommand = editCommand;
        Remote = def.RemotePort is not null ? $"0.0.0.0:{def.RemotePort}" : def.Domain ?? "-";
    }

    /// <summary>所属分组（空 → default）</summary>
    public string GroupName { get; }

    public string Remote
    {
        get => _remote;
        set => SetProperty(ref _remote, value);
    }

    public TunnelUiState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
            }
        }
    }

    public string StateText => State switch
    {
        TunnelUiState.Registered => "● 运行中",
        TunnelUiState.Pending => "● 注册中",
        TunnelUiState.Failed => "● 失败",
        _ => "● 离线",
    };

    public IBrush StateBrush => State switch
    {
        TunnelUiState.Registered => Green,
        TunnelUiState.Pending => Yellow,
        TunnelUiState.Failed => Red,
        _ => Gray,
    };

    /// <summary>类型徽章色（tcp 青 / udp 紫 / http 蓝 / https 绿）</summary>
    public IBrush TypeBrush => Type switch
    {
        "tcp" => Teal,
        "udp" => Purple,
        "http" => Blue,
        "https" => Green,
        _ => Green,
    };

    public long Up
    {
        get => _up;
        set
        {
            if (SetProperty(ref _up, value))
                OnPropertyChanged(nameof(FlowText));
        }
    }

    public long Down
    {
        get => _down;
        set
        {
            if (SetProperty(ref _down, value))
                OnPropertyChanged(nameof(FlowText));
        }
    }

    /// <summary>流量文本："▲ 12.3KB  ▼ 45.6KB"</summary>
    public string FlowText => $"▲ {FormatBytes(Up)}  ▼ {FormatBytes(Down)}";

    /// <summary>本地 → 远端（行内展示）</summary>
    public string RouteText => $"{Local}  →  {Remote}";

    public ICommand RemoveCommand { get; }

    public ICommand EditCommand { get; }

    private static string FormatBytes(long v) => v switch
    {
        >= 1024 * 1024 => $"{v / 1024.0 / 1024.0:F1}MB",
        >= 1024 => $"{v / 1024.0:F1}KB",
        _ => $"{v}B",
    };
}
