using Aeterni.Tunnel.Desktop.Dialogs;
using Aeterni.Tunnel.Desktop.ViewModels;
using Aeterni.Tunnel.Engine.Hosting;
using Avalonia.Controls;

namespace Aeterni.Tunnel.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    /// <summary>内容根（启动过渡淡入用）</summary>
    public Control ContentRoot => MainRoot;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainWindowViewModel();
        DataContext = _vm;

        // 弹窗承载：添加/修改隧道
        _vm.EditTunnelRequested += async def =>
        {
            var dialog = new TunnelEditorWindow(def, _vm.AllowPorts);
            await dialog.ShowDialog(this);
            if (dialog.Result is not null)
                await _vm.ApplyTunnelAsync(dialog.Result, dialog.IsEdit);
        };

        // 弹窗承载：启动时连接配置缺失/无效 → 引导填写并保存连接
        _vm.ConnectionSetupRequested += async () =>
        {
            var dialog = new ConnectionDialog(_vm.ServerAddr, _vm.ServerPort, _vm.Token, _vm.UseTls);
            await dialog.ShowDialog(this);
            if (dialog.Confirmed)
                _vm.ApplyConnectionSettings(dialog.Address, dialog.Port, dialog.Token, dialog.UseTls);
        };

        // 弹窗承载：删除确认
        _vm.RemoveTunnelRequested += async proxyId =>
        {
            var dialog = new ConfirmDialog("删除隧道", $"确定删除隧道 \"{proxyId}\"？该操作会同步更新 agent.toml。");
            await dialog.ShowDialog(this);
            if (dialog.Confirmed)
                await _vm.RemoveTunnelAsync(proxyId);
        };

        // 窗口打开：读取 agent.toml（首启无配置 → 引导填写；有配置 → 自动填充并自动连接）
        Opened += async (_, _) => await _vm.OnLoadedAsync();

        // 窗口关闭：断开连接、释放引擎
        Closed += async (_, _) => await _vm.DisposeAsync();
    }
}
