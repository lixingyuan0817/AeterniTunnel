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
            // 启动先播放 AETERNI 动画（独立 Splash 窗口），完成后内容层交叉淡变衔接：
            // splash 内容淡出 + 主窗口内容淡入同时进行（窗口背景同为深色毛玻璃，视觉连续）
            SplashWindow splash = null!;
            splash = new SplashWindow(() =>
                Dispatcher.UIThread.Post(async () =>
                {
                    var main = new MainWindow();
                    main.ContentRoot.Opacity = 0;
                    desktop.MainWindow = main;
                    main.Show();

                    const double fadeOut = 0.35, fadeIn = 0.55;
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < fadeIn)
                    {
                        var t = sw.Elapsed.TotalSeconds;
                        splash.ContentRoot.Opacity = Smooth(1 - t / fadeOut);
                        main.ContentRoot.Opacity = Smooth(t / fadeIn);
                        await Task.Delay(16);
                    }
                    splash.ContentRoot.Opacity = 0;
                    main.ContentRoot.Opacity = 1;
                    splash.Close();
                }));
            splash.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>smoothstep 缓动（过渡更丝滑，非线性）</summary>
    private static double Smooth(double x)
        => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);
}