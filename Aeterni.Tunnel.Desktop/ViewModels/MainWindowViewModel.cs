using System.Collections.ObjectModel;
using System.Windows.Input;
using Aeterni.Tunnel.Desktop.Services;
using Aeterni.Tunnel.Engine.Client;
using Aeterni.Tunnel.Common;
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
    private AgentOptions? _activeOptions;   // 当前连接的服务端参数（保存设置时对比变化）
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
        NavigateLauncherCommand = new RelayCommand(() => Navigate("launcher"));
        // 客户端常连：无手动连接/断开；未连接（重连中）时隧道操作禁用
        AddTunnelCommand = new RelayCommand(() => EditTunnelRequested?.Invoke(null), () => IsConnected);
        LoadConfigCommand = new RelayCommand(() => _ = LoadConfigAsync());
        SelectServerCommand = new RelayCommand(s => { if (s is ServerCardViewModel card) SelectedServer = card; });
        SaveSettingsCommand = new RelayCommand(SaveSettings);

        // 事件驱动：连接建立/断开即时刷新 UI（不依赖每秒轮询）
        Toasts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ToastCount));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshTick();
        _timer.Start();

        AddLog("欢迎使用 AETERNI TUNNEL 桌面客户端（ATC）");
        SeedSampleServers();
    }

    // ═════════ 页面导航 ═════════

    public object CurrentPage { get; private set; } = new HomePage();

    public bool HomeVisible => CurrentPage is HomePage;

    public bool TunnelsVisible => CurrentPage is TunnelsPage;

    public bool SettingsVisible => CurrentPage is SettingsPage;

    public bool LauncherVisible => CurrentPage is LauncherPage;

    public IBrush NavHomeBrush => CurrentPage is HomePage ? NavActiveBrush : NavMutedBrush;

    public IBrush NavTunnelsBrush => CurrentPage is TunnelsPage ? NavActiveBrush : NavMutedBrush;

    public IBrush NavSettingsBrush => CurrentPage is SettingsPage ? NavActiveBrush : NavMutedBrush;

    public IBrush NavLauncherBrush => CurrentPage is LauncherPage ? NavActiveBrush : NavMutedBrush;

    private IBrush NavActiveBrush => IsDarkTheme ? NavActiveDark : NavActiveLight;

    private IBrush NavMutedBrush => IsDarkTheme ? NavMutedDark : NavMutedLight;

    public ICommand NavigateHomeCommand { get; }

    public ICommand NavigateTunnelsCommand { get; }

    public ICommand NavigateSettingsCommand { get; }

    public ICommand NavigateLauncherCommand { get; }

    /// <summary>开服页实例分组（左栏：组 → 实例，示例数据后续接入 Launcher 服务）</summary>
    public ObservableCollection<ServerGroupViewModel> ServerGroups { get; } = [];

    private ServerCardViewModel? _selectedServer;
    public ServerCardViewModel? SelectedServer
    {
        get => _selectedServer;
        set { _selectedServer = value; OnPropertyChanged(); }
    }

    public RelayCommand SelectServerCommand { get; }

    private void SeedSampleServers()
    {
        var mine = new ServerGroupViewModel { Name = "我的服务器" };
        mine.Items.Add(new ServerCardViewModel
        {
            Name = "lyzp-mc", Template = "Minecraft Paper 1.21", Java = "Java 21",
            Port = ":25565", Stats = "运行 2h31m · ↑1.2MB ↓800KB", IsRunning = true,
        });
        mine.Items.Add(new ServerCardViewModel
        {
            Name = "skyblock", Template = "Minecraft Vanilla 1.21", Java = "Java 21",
            Port = ":25566", IsRunning = false,
        });
        ServerGroups.Add(mine);

        var archive = new ServerGroupViewModel { Name = "存档服" };
        archive.Items.Add(new ServerCardViewModel
        {
            Name = "survival", Template = "Minecraft Paper 1.20", Java = "Java 17",
            Port = ":25567", IsRunning = false,
        });
        ServerGroups.Add(archive);

        SelectedServer = mine.Items[0];
    }

    private void Navigate(object page)
    {
        if (CurrentPage == page)
            return;
        CurrentPage = page;
        OnPropertyChanged(nameof(CurrentPage));   // 关键：ContentControl 绑定刷新（此前缺失导致切换无效）
        OnPropertyChanged(nameof(HomeVisible));
        OnPropertyChanged(nameof(TunnelsVisible));
        OnPropertyChanged(nameof(SettingsVisible));
        OnPropertyChanged(nameof(LauncherVisible));
        OnPropertyChanged(nameof(NavHomeBrush));
        OnPropertyChanged(nameof(NavTunnelsBrush));
        OnPropertyChanged(nameof(NavSettingsBrush));
        OnPropertyChanged(nameof(NavLauncherBrush));
    }

    // ═════════ 明暗主题（明亮 / 黑暗 / 跟随系统，选择持久化到 agent.toml 的 theme 键） ═════════

    /// <summary>主题模式（保存值与 agent.toml theme 键一致：light/dark/system）</summary>
    public enum ThemeMode { Light, Dark, System }

    private ThemeMode _themeMode = ThemeMode.Dark;

    /// <summary>当前实际生效是否为暗色（跟随系统模式下由系统实时决定）</summary>
    public bool IsDarkTheme => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public bool IsLightChecked { get => _themeMode == ThemeMode.Light; set { if (value) SelectTheme(ThemeMode.Light); } }

    public bool IsDarkChecked { get => _themeMode == ThemeMode.Dark; set { if (value) SelectTheme(ThemeMode.Dark); } }

    public bool IsSystemChecked { get => _themeMode == ThemeMode.System; set { if (value) SelectTheme(ThemeMode.System); } }

    public void SelectTheme(ThemeMode mode)
    {
        if (_themeMode == mode)
            return;
        _themeMode = mode;
        ApplyTheme();
        SaveConfig();   // 主题选择持久化到 agent.toml 的 theme 键
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = _themeMode switch
            {
                ThemeMode.Light => ThemeVariant.Light,
                ThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,   // 跟随系统
            };
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightChecked));
        OnPropertyChanged(nameof(IsDarkChecked));
        OnPropertyChanged(nameof(IsSystemChecked));
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
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBarText));
                OnPropertyChanged(nameof(StatusBarBg));
                OnPropertyChanged(nameof(TunnelsNavEnabled));
                AddTunnelCommand.RaiseCanExecuteChanged();
                // 断联（含重连中）时若停留在隧道页 → 跳回首页；隧道页仅在连接后可用
                if (!value && CurrentPage == "tunnels")
                    Navigate("home");
            }
        }
    }

    /// <summary>顶栏「隧道」导航是否可用（未连接/重连中禁用）</summary>
    public bool TunnelsNavEnabled => IsConnected;

    public IBrush StatusBrush => IsConnected ? Green : Yellow;

    /// <summary>服务端 allowPorts 白名单（登录后由 PortPolicyMessage 下发；空 = 不限制）——添加隧道前置校验</summary>
    public IReadOnlyList<int> AllowPorts => _service?.PortPolicy?.AllowPorts ?? [];

    /// <summary>底部状态栏背景（连接：绿色微光；未连接：黄色微光——状态直接反映成 bar 颜色）</summary>
    public IBrush StatusBarBg => IsConnected ? BarConnectedBg : BarDisconnectedBg;

    private static readonly IBrush BarConnectedBg = SolidColorBrush.Parse("#1634C759");
    private static readonly IBrush BarDisconnectedBg = SolidColorBrush.Parse("#16FBBF24");

    public string StatusBarText => IsConnected
        ? $"已连接 · {Tunnels.Count} 隧道   ▲ {FormatRate(_upRate)}  ▼ {FormatRate(_downRate)}"
        : $"未连接 · {Tunnels.Count} 隧道";

    /// <summary>版本号（读程序集版本，发版时 CI 以 tag 覆盖）</summary>
    public string VersionText
    {
        get
        {
            var v = typeof(MainWindowViewModel).Assembly.GetName().Version;
            return v is null ? "ATC" : $"v{v.Major}.{v.Minor}.{v.Build} · ATC";
        }
    }

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

    public RelayCommand AddTunnelCommand { get; }

    public ICommand LoadConfigCommand { get; }

    public ICommand SaveSettingsCommand { get; }

    /// <summary>请求打开隧道编辑弹窗（null=添加，非 null=修改）——View 层承载弹窗</summary>
    public event Action<ProxyDefinition?>? EditTunnelRequested;

    /// <summary>请求确认删除隧道——View 层承载确认弹窗后调用 RemoveTunnelAsync</summary>
    public event Action<string>? RemoveTunnelRequested;

    /// <summary>启动时配置缺失/无效：请求弹窗填写连接设置——View 层承载</summary>
    public event Action? ConnectionSetupRequested;

    // ═════════ 首启与配置持久化 ═════════

    public static string ConfigPath => AgentTomlWriter.DefaultPath;

    private bool _isFirstRun;

    public bool IsFirstRun
    {
        get => _isFirstRun;
        private set => SetProperty(ref _isFirstRun, value);
    }

    public string FirstRunHint => "缺少连接配置：请完善服务端地址/端口/令牌，保存后自动连接并写入 agent.toml";

    /// <summary>窗口打开后调用：读配置 → 恢复主题 + 填充表单 + 导入隧道；配置有效自动连接，无效弹窗引导填写</summary>
    public async Task OnLoadedAsync()
    {
        var cfg = ConfigLoader.LoadAgentConfig(ConfigPath);
        if (cfg is null)
        {
            IsFirstRun = true;
            AddLog("未找到 agent.toml，请完善连接设置");
            ConnectionSetupRequested?.Invoke();
            return;
        }

        // 恢复主题（theme 键由本客户端写入，Engine 解析器忽略未知键）
        try
        {
            var kv = MinimalToml.Parse(File.ReadAllText(ConfigPath));
            if (kv.TryGetValue("theme", out var v) && v is string s)
            {
                var mode = s.ToLowerInvariant() switch
                {
                    "light" => ThemeMode.Light,
                    "system" => ThemeMode.System,
                    _ => ThemeMode.Dark,
                };
                if (mode != _themeMode)
                {
                    _themeMode = mode;
                    ApplyTheme();
                }
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

        var valid = !string.IsNullOrWhiteSpace(cfg.ServerAddr)
            && cfg.ServerPort is >= 1 and <= 65535
            && !string.IsNullOrWhiteSpace(cfg.Token);
        if (valid)
        {
            Connect();   // 后续启动：直接读取配置并自动连接
        }
        else
        {
            IsFirstRun = true;
            AddLog("配置文件缺少服务端信息（地址/端口/令牌），请完善连接设置");
            ConnectionSetupRequested?.Invoke();
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
                _pendingDefs, _themeMode.ToString().ToLowerInvariant());
            File.WriteAllText(ConfigPath, text);
            IsFirstRun = false;
            AddLog($"配置已保存：{ConfigPath}");
        }
        catch (Exception ex)
        {
            AddLog($"保存配置失败：{ex.Message}");
        }
    }

    // ═════════ 连接（常连，自动重连） ═════════

    /// <summary>右上角 Toast 提示集合（新到顶部，最多 5 条，3 秒自动消失）</summary>
    public ObservableCollection<ToastItemViewModel> Toasts { get; } = [];

    public int ToastCount => Toasts.Count;

    /// <summary>弹出 Toast（任意线程可调，内部调度到 UI 线程）</summary>
    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
        => Dispatcher.UIThread.Post(() =>
        {
            Toasts.Insert(0, new ToastItemViewModel(message, kind, t => Toasts.Remove(t)));
            while (Toasts.Count > 5)
                Toasts.RemoveAt(Toasts.Count - 1);
        });

    private void Connect()
    {
        if (_service is not null)
            return;
        if (!int.TryParse(ServerPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            AddLog("服务端端口无效");
            return;
        }

        SaveConfig();   // 记录服务端信息到 agent.toml

        var options = new AgentOptions(ServerAddr.Trim(), port, Token.Trim(), ClientId.Trim(), UseTls: UseTls);
        _activeOptions = options;
        var svc = new AgentClientService(options);
        svc.LogReceived += OnLogReceived;
        svc.ProxyRegistered += OnProxyRegistered;
        svc.Connected += OnEngineConnected;
        svc.Disconnected += OnEngineDisconnected;
        _service = svc;

        foreach (var d in _pendingDefs)
            _ = svc.AddTunnelAsync(d);

        _ = svc.StartAsync();   // 初次连接失败由引擎后台自动重连
        AddLog($"正在连接 {options.ServerAddr}:{port}{(options.UseTls ? "（TLS）" : "")}…");
    }

    /// <summary>用当前表单配置重建连接（保存设置/连接参数变更时调用）</summary>
    private void Reconnect()
    {
        var svc = _service;
        _service = null;
        if (svc is not null)
        {
            svc.LogReceived -= OnLogReceived;
            svc.ProxyRegistered -= OnProxyRegistered;
            svc.Connected -= OnEngineConnected;
            svc.Disconnected -= OnEngineDisconnected;
            _ = svc.DisposeAsync();
        }
        _registered.Clear();
        _failed.Clear();
        IsConnected = false;
        Connect();
    }

    /// <summary>设置页保存：写配置；连接参数变化则重建连接（修复改 IP 后日志仍是旧地址的问题）</summary>
    private void SaveSettings()
    {
        SaveConfig();
        ApplyServerSettingsIfChanged();
    }

    /// <summary>连接参数（地址/端口/令牌/客户端ID/TLS）变化 → 重建连接；未变化则不动</summary>
    private void ApplyServerSettingsIfChanged()
    {
        var portOk = int.TryParse(ServerPort.Trim(), out var port) && port is >= 1 and <= 65535;
        var changed = _activeOptions is null || !portOk
            || _activeOptions.ServerAddr != ServerAddr.Trim()
            || _activeOptions.ServerPort != port
            || _activeOptions.Token != Token.Trim()
            || _activeOptions.ClientId != ClientId.Trim()
            || _activeOptions.UseTls != UseTls;
        if (changed)
        {
            AddLog("连接配置已变更，正在重新连接…");
            Reconnect();
        }
    }

    /// <summary>连接设置弹窗确认后应用：写配置并（重新）连接</summary>
    public void ApplyConnectionSettings(string addr, int port, string token, bool useTls)
    {
        ServerAddr = addr;
        ServerPort = port.ToString();
        Token = token;
        UseTls = useTls;
        SaveConfig();
        Reconnect();
    }

    // ═════════ 引擎事件（后台线程 → UI 线程） ═════════

    private void OnLogReceived(string line)
        => Dispatcher.UIThread.Post(() => AddLog(line));

    /// <summary>连接建立（含重连成功）——事件驱动，即时更新 UI</summary>
    private void OnEngineConnected()
        => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = true;
            ShowToast("已连接到服务端", ToastKind.Success);
        });

    /// <summary>连接断开（断线进入重连 / 停止）——即时更新 UI 并提示</summary>
    private void OnEngineDisconnected()
        => Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            ShowToast("连接断开，正在自动重连…", ToastKind.Error);
        });

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
                // 服务端拒绝（端口白名单/数量上限/vhost 未启用等）→ 右上角提示原因
                var name = Tunnels.FirstOrDefault(t => t.ProxyId == id)?.Local ?? id;
                ShowToast($"隧道「{name}」创建失败：{addr ?? "未知原因"}", ToastKind.Error);
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
        Logs.Add(LogItemViewModel.Create(DateTime.Now, line));
        while (Logs.Count > 500)
            Logs.RemoveAt(0);
    }

    // ═════════ 每秒刷新 ═════════

    private void RefreshTick()
    {
        var svc = _service;
        IsConnected = svc?.IsConnected ?? false;
        // 未连接/重连中：隧道列表置灰禁用（编辑/删除/添加均不可用）
        foreach (var t in Tunnels)
            t.IsEnabled = IsConnected;
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
        IsFirstRun = false;
        AddLog($"加载配置 {path}：{defs.Count} 条隧道");
        SaveConfig();

        // 服务端参数变化 → 重建连接（Connect 自动注册 _pendingDefs）；未变化则向当前连接补齐隧道
        var before = _activeOptions;
        ApplyServerSettingsIfChanged();
        if (_service is not null && ReferenceEquals(before, _activeOptions))
        {
            foreach (var d in defs)
                await _service.AddTunnelAsync(d);
        }
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
