using System.Collections.ObjectModel;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>开服页左侧实例分组（组名 + 组内实例）</summary>
public sealed class ServerGroupViewModel
{
    public string Name { get; set; } = "";

    public ObservableCollection<ServerCardViewModel> Items { get; } = [];
}
