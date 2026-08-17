using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Aeterni.Tunnel.Desktop.ViewModels;
using Aeterni.Tunnel.Desktop.Views;

namespace Aeterni.Tunnel.Desktop;

/// <summary>页面标记 → View 映射（ContentControl 自动选模板渲染）</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        HomePage => new HomeView(),
        TunnelsPage => new TunnelsView(),
        SettingsPage => new SettingsView(),
        LauncherPage => new LauncherView(),
        _ => null,
    };

    public bool Match(object? data) =>
        data is HomePage or TunnelsPage or SettingsPage or LauncherPage;
}
