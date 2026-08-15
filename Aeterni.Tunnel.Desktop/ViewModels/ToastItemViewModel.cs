using Avalonia.Media;
using Avalonia.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>Toast 类型（决定左侧色条）</summary>
public enum ToastKind
{
    Success,
    Error,
    Info,
}

/// <summary>右上角 Toast 提示：3 秒后自动淡出移除（VM 集合管理）</summary>
public sealed class ToastItemViewModel : INotifyPropertyChanged
{
    private const double AutoCloseSeconds = 3.2;
    private const double FadeOutSeconds = 0.25;

    private bool _isClosing;
    private readonly DispatcherTimer _timer;
    private readonly Action<ToastItemViewModel> _onExpired;

    public string Message { get; }

    public ToastKind Kind { get; }

    /// <summary>左侧色条（Success 绿 / Error 红 / Info 蓝）</summary>
    public IBrush Accent { get; }

    /// <summary>当前透明度（关闭前淡出动画）</summary>
    public double Opacity => _isClosing ? 0 : 1;

    public ToastItemViewModel(string message, ToastKind kind, Action<ToastItemViewModel> onExpired)
    {
        Message = message;
        Kind = kind;
        _onExpired = onExpired;
        Accent = kind switch
        {
            ToastKind.Success => SolidColorBrush.Parse("#34C759"),
            ToastKind.Error => SolidColorBrush.Parse("#F87171"),
            _ => SolidColorBrush.Parse("#60A5FA"),
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoCloseSeconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _isClosing = true;
            OnPropertyChanged(nameof(Opacity));
            // 等淡出动画播完再真正移除
            var remove = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FadeOutSeconds) };
            remove.Tick += (_, _) =>
            {
                remove.Stop();
                _onExpired(this);
            };
            remove.Start();
        };
        _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
