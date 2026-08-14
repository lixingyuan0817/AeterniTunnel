using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Aeterni.Tunnel.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动先播放 AETERNI 动画（独立 Splash 窗口），完成后打开主窗口
            SplashWindow splash = null!;
            splash = new SplashWindow(() =>
                Dispatcher.UIThread.Post(() =>
                {
                    desktop.MainWindow = new MainWindow();
                    desktop.MainWindow.Show();
                    splash.Close();
                }));
            splash.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}