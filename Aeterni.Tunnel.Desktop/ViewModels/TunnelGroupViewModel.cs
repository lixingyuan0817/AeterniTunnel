using System.Collections.ObjectModel;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>隧道分组（分组头 + 成员行）</summary>
public sealed class TunnelGroupViewModel
{
    public TunnelGroupViewModel(string name)
    {
        Name = name;
    }

    /// <summary>分组名（default / 自定义）</summary>
    public string Name { get; }

    /// <summary>分组内隧道行</summary>
    public ObservableCollection<TunnelItemViewModel> Items { get; } = [];
}
