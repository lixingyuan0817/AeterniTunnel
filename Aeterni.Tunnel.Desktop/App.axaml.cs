using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Diagnostics;

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
            // 启动先播放 AETERNI 动画（独立 Splash 窗口），完成后主窗口淡入、Splash 淡出，重叠衔接
            SplashWindow splash = null!;
            splash = new SplashWindow(() =>
                Dispatcher.UIThread.Post(async () =>
                {
                    var main = new MainWindow { Opacity = 0 };
                    desktop.MainWindow = main;
                    main.Show();

                    // 手动逐帧渐变（Transitions 对窗口 Opacity 跨平台不可靠）：
                    // splash 淡出 0.3s 与 main 淡入 0.45s 同时进行
                    const double fadeOut = 0.30, fadeIn = 0.45;
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < fadeIn)
                    {
                        var t = sw.Elapsed.TotalSeconds;
                        splash.Opacity = Math.Max(0, 1 - t / fadeOut);
                        main.Opacity = Math.Min(1, t / fadeIn);
                        await Task.Delay(16);
                    }
                    splash.Opacity = 0;
                    main.Opacity = 1;
                    splash.Close();
                }));
            splash.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}