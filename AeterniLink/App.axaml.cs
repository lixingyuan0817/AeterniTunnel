using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using System;

namespace AeterniLink;

public partial class App : Application
{
    private TrayIcon? _tray;
    private Window? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 关闭主界面后程序驻留托盘继续运行，仅通过托盘「退出」显式结束
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;

            // 点关闭（红绿灯 ✕ / Alt+F4）：隐藏到托盘而非退出
            _mainWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                _mainWindow.Hide();
            };

            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>系统托盘：mac 菜单栏 / win 通知区；菜单含「打开主界面」「退出」</summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        using var stream = AssetLoader.Open(new Uri("avares://AeterniLink/Assets/icon.png"));
        var icon = new WindowIcon(stream);

        var openItem = new NativeMenuItem("打开主界面");
        openItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            desktop.Shutdown();
        };

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "AeterniLink",
            IsVisible = true,
            Menu = menu,
        };
    }

    /// <summary>从托盘恢复主界面：显示 + 还原最小化 + 聚焦</summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}
