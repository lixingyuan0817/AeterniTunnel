using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Aeterni.Tunnel.Desktop;

/// <summary>
/// 启动动画窗口：复刻 Web 登录页 AETERNI 字母方块汇聚动画（Avalonia 无 CSS perspective 3D，
/// 用 2D 平移汇聚 + 旋转近似）。播放完成后回调打开主窗口并关闭自身。
/// </summary>
public partial class SplashWindow : Window
{
    private const double BoxW = 46;
    private const double BoxH = 54;
    private const double Step = BoxW + 8;   // 间距
    private static readonly Color Accent = Color.Parse("#34C759");

    private readonly List<(Border Box, TranslateTransform Move, RotateTransform Spin, TransformGroup Group)> _letters = [];
    private readonly DispatcherTimer _timer;
    private readonly Action _onDone;
    private readonly DateTime _start = DateTime.UtcNow;

    /// <summary>内容根（启动过渡淡出用）</summary>
    public Control ContentRoot => RootPanel;

    public SplashWindow(Action onDone)
    {
        InitializeComponent();
        _onDone = onDone;
        const string brand = "AETERNI";
        for (var i = 0; i < brand.Length; i++)
        {
            var box = new Border
            {
                Width = BoxW,
                Height = BoxH,
                BorderBrush = new SolidColorBrush(Accent),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x34, 0xC7, 0x59)),
                Child = new TextBlock
                {
                    Text = brand[i].ToString(),
                    FontSize = 26,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            var move = new TranslateTransform { X = (brand.Length - i) * Step };
            var spin = new RotateTransform();
            var group = new TransformGroup { Children = { move, spin } };
            box.RenderTransform = group;
            LogoPanel.Children.Add(box);
            _letters.Add((box, move, spin, group));
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _start).TotalSeconds;

        // 阶段1：字母方块从右向左汇聚（每字母延迟递增，easeOutCubic）
        for (var i = 0; i < _letters.Count; i++)
        {
            var (_, move, spin, _) = _letters[i];
            var delay = 0.15 + i * 0.13;
            var p = Clamp((t - delay) / 0.7);
            var ease = 1 - Math.Pow(1 - p, 3);
            move.X = (1 - ease) * ((_letters.Count - i) * Step);

            // 阶段2：汇聚后字母整体旋转 720°（近似 CSS rotateY 翻转）
            var rd = delay + 0.55 + i * 0.06;
            var rp = Clamp((t - rd) / 0.8);
            spin.Angle = rp * 720;
        }

        // 阶段3：TUNNEL 副标题淡入
        SubText.Opacity = Clamp((t - 1.9) / 0.45);

        if (t >= 2.7)
        {
            _timer.Stop();
            _onDone();
        }
    }

    private static double Clamp(double v) => v is < 0 ? 0 : v > 1 ? 1 : v;
}
