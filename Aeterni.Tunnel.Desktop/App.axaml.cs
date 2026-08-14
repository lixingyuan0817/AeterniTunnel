using Avalonia;
using Avalonia.Animation;
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
            // 启动先播放 AETERNI 动画（独立 Splash 窗口），完成后淡出并淡入主窗口
            SplashWindow splash = null!;
            splash = new SplashWindow(() =>
                Dispatcher.UIThread.Post(async () =>
                {
                    splash.Transitions ??= new Transitions();
                    splash.Transitions.Add(new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(260),
                    });
                    splash.Opacity = 0;
                    await Task.Delay(260);
                    splash.Close();

                    var main = new MainWindow();
                    main.Transitions ??= new Transitions();
                    main.Transitions.Add(new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(400),
                    });
                    main.Opacity = 0;
                    desktop.MainWindow = main;
                    main.Show();
                    main.Opacity = 1;
                }));
            splash.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}