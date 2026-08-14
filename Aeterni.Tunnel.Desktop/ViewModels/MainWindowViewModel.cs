using System.Collections.ObjectModel;
using System.Windows.Input;
using Aeterni.Tunnel.Desktop.Services;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Engine.Config;
using Aeterni.Tunnel.Engine.Hosting;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Aeterni.Tunnel.Desktop.ViewModels;

/// <summary>
/// 主窗口 VM：页面导航（首页/隧道/设置）+ 连接 + 分组隧道 + 日志 + 明暗主题 + 配置持久化。
/// 添加/修改/删除隧道经弹窗事件（EditTunnelRequested/RemoveTunnelRequested）由 View 层承载。
/// </summary>
public sealed class MainWindowViewModel : ObservableBase, IAsyncDisposable
{
    private static readonly IBrush Green = SolidColorBrush.Parse("#34C759");
    private static readonly IBrush Yellow = SolidColorBrush.Parse("#FBBF24");
    private static readonly IBrush NavActiveDark = SolidColorBrush.Parse("#34C759");
    private static readonly IBrush NavActiveLight = SolidColorBrush.Parse("#15803D");
    private static readonly IBrush NavMutedDark = SolidColorBrush.Parse("#94A3B8");
    private static readonly IBrush NavMutedLight = SolidColorBrush.Parse("#64748B");

    private AgentClientService? _service;
    private readonly HashSet<string> _registered = [];
    private readonly HashSet<string> _failed = [];
    private readonly List<ProxyDefinition> _pendingDefs = [];
    private readonly DispatcherTimer _timer;

    private bool _isConnected;
    private long _lastUp;
    private long _lastDown;
    private DateTime _lastSample = DateTime.UtcNow;
    private double _upRate;
    private double _downRate;

    public MainWindowViewModel()
    {
        NavigateHomeCommand = new RelayCommand(() => Navigate("home"));
        NavigateTunnelsCommand = new RelayCommand(() => Navigate("tunnels"));
        NavigateSettingsCommand = new RelayCommand(() => Navigate("settings"));
        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(Disconnect);
        AddTunnelCommand = new RelayCommand(() => EditTunnelRequested?.Invoke(null));
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        LoadConfigCommand = new RelayCommand(() => _ = LoadConfigAsync());
        SaveSettingsCommand = new RelayCommand(SaveConfig);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshTick();
        _timer.Start();

        AddLog("欢迎使用 AETERNI TUNNEL 桌面客户端（ATC）");
    }

    // ═════════ 页面导航 ═════════

    public string CurrentPage { get; private set; } = "home";

    public bool HomeVisible => CurrentPage == "home";

    public bool TunnelsVisible => CurrentPage == "tunnels";

    public bool SettingsVisible => CurrentPage == "settings";

    public IBrush NavHomeBrush => CurrentPage == "home" ? NavActiveBrush : NavMutedBrush;

    public IBrush NavTunnelsBrush => CurrentPage == "tunnels" ? NavActiveBrush : NavMutedBrush;

    public IBrush NavSettingsBrush => CurrentPage == "settings" ? NavActiveBrush : NavMutedBrush;

    private IBrush NavActiveBrush => IsDarkTheme ? NavActiveDark : NavActiveLight;

    private IBrush NavMutedBrush => IsDarkTheme ? NavMutedDark : NavMutedLight;

    public ICommand NavigateHomeCommand { get; }

    public ICommand NavigateTunnelsCommand { get; }

    public ICommand NavigateSettingsCommand { get; }

    private void Navigate(string page)
    {
        if (CurrentPage == page)
            return;
        CurrentPage = page;
        OnPropertyChanged(nameof(HomeVisible));
        OnPropertyChanged(nameof(TunnelsVisible));
        OnPropertyChanged(nameof(SettingsVisible));
        OnPropertyChanged(nameof(NavHomeBrush));
        OnPropertyChanged(nameof(NavTunnelsBrush));
        OnPropertyChanged(nameof(NavSettingsBrush));
    }

    // ═════════ 明暗主题 ═════════

    private bool _isDarkTheme = true;

    public bool IsDarkTheme => _isDarkTheme;

    public string ThemeToggleText => IsDarkTheme ? "🌙 深色" : "☀ 亮色";

    public ICommand ToggleThemeCommand { get; }

    public void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme();
        SaveConfig();   // 主题选择持久化到 agent.toml 的 theme 键
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeToggleText));
        OnPropertyChanged(nameof(NavHomeBrush));
        OnPropertyChanged(nameof(NavTunnelsBrush));
        OnPropertyChanged(nameof(NavSettingsBrush));
    }

    // ═════════ 连接表单（设置页） ═════════

    private string _serverAddr = "127.0.0.1";
    public string ServerAddr { get => _serverAddr; set => SetProperty(ref _serverAddr, value); }

    private string _serverPort = "7000";
    public string ServerPort { get => _serverPort; set => SetProperty(ref _serverPort, value); }

    private string _token = "";
    public string Token { get => _token; set => SetProperty(ref _token, value); }

    private string _clientId = "";
    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }

    private bool _useTls;
    public bool UseTls { get => _useTls; set => SetProperty(ref _useTls, value); }

    // ═════════ 连接状态 ═════════

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusText => IsConnected ? "已连接" : "未连接";

    public IBrush StatusBrush => IsConnected ? Green : Yellow;

    public string StatusBarText => IsConnected
        ? $"{Tunnels.Count} 隧道   ▲ {FormatRate(_upRate)}  ▼ {FormatRate(_downRate)}"
        : $"{Tunnels.Count} 隧道";

    // ═════════ 首页统计 ═════════

    public int TunnelCount => Tunnels.Count;

    public int GroupCount => TunnelGroups.Count;

    public string UpRateText => IsConnected ? FormatRate(_upRate) : "-";

    public string DownRateText => IsConnected ? FormatRate(_downRate) : "-";

    // ═════════ 隧道（分组视图） ═════════

    public ObservableCollection<TunnelItemViewModel> Tunnels { get; } = [];

    public ObservableCollection<TunnelGroupViewModel> TunnelGroups { get; } = [];

    public ObservableCollection<LogItemViewModel> Logs { get; } = [];

    public string TunnelCountText => $"隧道列表 ({Tunnels.Count})";

    // ═════════ 命令 ═════════

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand AddTunnelCommand { get; }

    public ICommand LoadConfigCommand { get; }

    public ICommand SaveSettingsCommand { get; }

    /// <summary>请求打开隧道编辑弹窗（null=添加，非 null=修改）——View 层承载弹窗</summary>
    public event Action<ProxyDefinition?>? EditTunnelRequested;

    /// <summary>请求确认删除隧道——View 层承载确认弹窗后调用 RemoveTunnelAsync</summary>
    public event Action<string>? RemoveTunnelRequested;

    // ═════════ 首启与配置持久化 ═════════

    public static string ConfigPath => AgentTomlWriter.DefaultPath;

    private bool _isFirstRun;

    public bool IsFirstRun
    {
        get => _isFirstRun;
        private set => SetProperty(ref _isFirstRun, value);
    }

    public string FirstRunHint => "首次使用：请到「设置」页填写服务端信息，保存后将自动写入 agent.toml";

    /// <summary>窗口打开后调用：读配置 → 恢复主题 + 填充表单 + 导入隧道；有 token 自动连接</summary>
    public async Task OnLoadedAsync()
    {
        var cfg = ConfigLoader.LoadAgentConfig(ConfigPath);
        if (cfg is null)
        {
            IsFirstRun = true;
            return;
        }

        // 恢复主题（theme 键由本客户端写入，Engine 解析器忽略未知键）
        try
        {
            var kv = MinimalToml.Parse(File.ReadAllText(ConfigPath));
            if (kv.TryGetValue("theme", out var v) && v is string s && s.Equals("light", StringComparison.OrdinalIgnoreCase))
            {
                _isDarkTheme = false;
                ApplyTheme();
            }
        }
        catch { /* 读取失败不影响启动 */ }

        ServerAddr = cfg.ServerAddr;
        ServerPort = cfg.ServerPort.ToString();
        Token = cfg.Token;
        ClientId = cfg.ClientId;
        UseTls = cfg.UseTls;

        var defs = ConfigLoader.ToProxyDefinitions(cfg);
        _pendingDefs.Clear();
        _pendingDefs.AddRange(defs);
        IsFirstRun = false;
        AddLog($"已加载配置 {ConfigPath}：{defs.Count} 条隧道");

        if (!string.IsNullOrWhiteSpace(cfg.Token))
        {
            Connect();   // 后续启动：直接读取配置并自动连接
        }
        else
        {
            IsFirstRun = true;
        }
        RefreshTick();
    }

    private void SaveConfig()
    {
        try
        {
            if (!int.TryParse(ServerPort.Trim(), out var port) || port is < 1 or > 65535)
                return;
            var text = AgentTomlWriter.Build(ServerAddr.Trim(), port, Token.Trim(), ClientId.Trim(), UseTls,
                _pendingDefs, IsDarkTheme ? "dark" : "light");
            File.WriteAllText(ConfigPath, text);
            IsFirstRun = false;
            AddLog($"配置已保存：{ConfigPath}");
        }
        catch (Exception ex)
        {
            AddLog($"保存配置失败：{ex.Message}");
        }
    }

    // ═════════ 连接 / 断开 ═════════

    private void Connect()
    {
        if (_service is not null)
        {
            AddLog("已在连接状态，请先断开");
            return;
        }
        if (!int.TryParse(ServerPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            AddLog("服务端端口无效");
            return;
        }

        SaveConfig();   // 记录服务端信息到 agent.toml

        var options = new AgentOptions(ServerAddr.Trim(), port, Token.Trim(), ClientId.Trim(), UseTls: UseTls);
        var svc = new AgentClientService(options);
        svc.LogReceived += OnLogReceived;
        svc.ProxyRegistered += OnProxyRegistered;
        _service = svc;

        foreach (var d in _pendingDefs)
            _ = svc.AddTunnelAsync(d);

        _ = svc.StartAsync();   // 初次连接失败由引擎后台自动重连
        AddLog($"正在连接 {options.ServerAddr}:{port}{(options.UseTls ? "（TLS）" : "")}…");
    }

    private void Disconnect()
    {
        var svc = _service;
        _service = null;
        if (svc is not null)
            _ = svc.StopAsync();
        _registered.Clear();
        _failed.Clear();
        IsConnected = false;
        AddLog("已断开连接");
    }

    // ═════════ 引擎事件（后台线程 → UI 线程） ═════════

    private void OnLogReceived(string line)
        => Dispatcher.UIThread.Post(() => AddLog(line));

    private void OnProxyRegistered(string id, bool ok, string? addr)
        => Dispatcher.UIThread.Post(() =>
        {
            if (ok)
            {
                _registered.Add(id);
                _failed.Remove(id);
            }
            else
            {
                _registered.Remove(id);
                _failed.Add(id);
            }
            var vm = Tunnels.FirstOrDefault(t => t.ProxyId == id);
            if (vm is not null)
            {
                if (ok && !string.IsNullOrEmpty(addr))
                    vm.Remote = addr;
                vm.State = !IsConnected ? TunnelUiState.Offline
                    : ok ? TunnelUiState.Registered
                    : TunnelUiState.Failed;
            }
        });

    private void AddLog(string line)
    {
        Logs.Add(LogItemViewModel.Create($"[{DateTime.Now:HH:mm:ss}] {line}"));
        while (Logs.Count > 500)
            Logs.RemoveAt(0);
    }

    // ═════════ 每秒刷新 ═════════

    private void RefreshTick()
    {
        var svc = _service;
        IsConnected = svc?.IsConnected ?? false;
        if (svc is null)
        {
            SyncGroups();
            return;
        }

        var defs = svc.Proxies;
        var traffic = svc.GetTraffic();

        for (var i = Tunnels.Count - 1; i >= 0; i--)
        {
            if (!defs.Any(d => d.ProxyId == Tunnels[i].ProxyId))
                Tunnels.RemoveAt(i);
        }

        long totalUp = 0, totalDown = 0;
        foreach (var d in defs)
        {
            var vm = Tunnels.FirstOrDefault(t => t.ProxyId == d.ProxyId);
            if (vm is null)
            {
                vm = new TunnelItemViewModel(d,
                    new RelayCommand(() => RemoveTunnelRequested?.Invoke(d.ProxyId)),
                    new RelayCommand(() => EditTunnelRequested?.Invoke(d)));
                Tunnels.Add(vm);
            }

            var (up, down) = traffic.TryGetValue(d.ProxyId, out var t) ? t : (0L, 0L);
            vm.Up = up;
            vm.Down = down;
            vm.State = !IsConnected ? TunnelUiState.Offline
                : _registered.Contains(d.ProxyId) ? TunnelUiState.Registered
                : _failed.Contains(d.ProxyId) ? TunnelUiState.Failed
                : TunnelUiState.Pending;
            totalUp += up;
            totalDown += down;
        }

        var now = DateTime.UtcNow;
        var secs = Math.Max(0.2, (now - _lastSample).TotalSeconds);
        _upRate = Math.Max(0, (totalUp - _lastUp) / secs);
        _downRate = Math.Max(0, (totalDown - _lastDown) / secs);
        _lastUp = totalUp;
        _lastDown = totalDown;
        _lastSample = now;

        SyncGroups();
        OnPropertyChanged(nameof(StatusBarText));
        OnPropertyChanged(nameof(TunnelCount));
        OnPropertyChanged(nameof(TunnelCountText));
        OnPropertyChanged(nameof(UpRateText));
        OnPropertyChanged(nameof(DownRateText));
    }

    /// <summary>分组视图与扁平隧道集合的增量同步</summary>
    private void SyncGroups()
    {
        var grouped = Tunnels.GroupBy(t => t.GroupName).OrderBy(g => g.Key).ToList();

        for (var i = TunnelGroups.Count - 1; i >= 0; i--)
        {
            if (!grouped.Any(g => g.Key == TunnelGroups[i].Name))
                TunnelGroups.RemoveAt(i);
        }

        foreach (var g in grouped)
        {
            var gv = TunnelGroups.FirstOrDefault(x => x.Name == g.Key);
            if (gv is null)
            {
                gv = new TunnelGroupViewModel(g.Key);
                TunnelGroups.Add(gv);
            }

            for (var i = gv.Items.Count - 1; i >= 0; i--)
            {
                if (!g.Any(t => t.ProxyId == gv.Items[i].ProxyId))
                    gv.Items.RemoveAt(i);
            }
            foreach (var t in g)
            {
                if (!gv.Items.Any(x => x.ProxyId == t.ProxyId))
                    gv.Items.Add(t);
            }
        }

        OnPropertyChanged(nameof(GroupCount));
    }

    // ═════════ 隧道操作（弹窗结果） ═════════

    /// <summary>应用弹窗结果：isEdit=false 添加；true 修改（先删后加，同 id 替换）</summary>
    public async Task ApplyTunnelAsync(ProxyDefinition def, bool isEdit)
    {
        var svc = _service;
        if (isEdit && svc is not null)
            await svc.RemoveTunnelAsync(def.ProxyId);

        _pendingDefs.RemoveAll(x => x.ProxyId == def.ProxyId);
        _pendingDefs.Add(def);
        _registered.Remove(def.ProxyId);
        _failed.Remove(def.ProxyId);

        if (svc is not null)
        {
            try
            {
                await svc.AddTunnelAsync(def);
                AddLog($"{(isEdit ? "修改" : "添加")}隧道 {def.ProxyId}");
            }
            catch (Exception ex)
            {
                AddLog($"{(isEdit ? "修改" : "添加")}隧道失败：{ex.Message}");
            }
        }
        else
        {
            AddLog($"已暂存隧道 {def.ProxyId}，连接后自动注册");
        }

        SaveConfig();
        RefreshTick();
    }

    /// <summary>删除隧道（确认弹窗后由 View 层调用）</summary>
    public async Task RemoveTunnelAsync(string proxyId)
    {
        var svc = _service;
        if (svc is not null)
            await svc.RemoveTunnelAsync(proxyId);
        _pendingDefs.RemoveAll(x => x.ProxyId == proxyId);
        _registered.Remove(proxyId);
        _failed.Remove(proxyId);
        AddLog($"移除隧道 {proxyId}");
        SaveConfig();
        RefreshTick();
    }

    /// <summary>加载 agent.toml：填充设置表单 + 导入隧道</summary>
    private async Task LoadConfigAsync()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
            path = "agent.toml";
        var cfg = ConfigLoader.LoadAgentConfig(path);
        if (cfg is null)
        {
            AddLog($"未找到配置文件：{path}");
            return;
        }

        ServerAddr = cfg.ServerAddr;
        ServerPort = cfg.ServerPort.ToString();
        Token = cfg.Token;
        ClientId = cfg.ClientId;
        UseTls = cfg.UseTls;

        var defs = ConfigLoader.ToProxyDefinitions(cfg);
        _pendingDefs.Clear();
        _pendingDefs.AddRange(defs);
        if (_service is not null)
        {
            foreach (var d in defs)
                await _service.AddTunnelAsync(d);
        }
        IsFirstRun = false;
        AddLog($"加载配置 {path}：{defs.Count} 条隧道");
        SaveConfig();
        RefreshTick();
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / 1024 / 1024:F1}MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:F1}KB/s";
        return $"{bytesPerSec:F0}B/s";
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        if (_service is not null)
        {
            await _service.DisposeAsync();
            _service = null;
        }
    }
}
