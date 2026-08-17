using System.Diagnostics;
using Aeterni.Tunnel.Launcher.Models;

namespace Aeterni.Tunnel.Launcher;

/// <summary>
/// 游戏服务器实例：进程生命周期管理（启动/优雅停止/崩溃重启+熔断）、
/// 控制台管道（stdout/stdin）、进程级环境注入、首次运行文件写入。
/// 每个实例对应工作空间 servers/&lt;名&gt;/。
/// </summary>
public sealed class ServerInstance : IAsyncDisposable
{
    private const int MaxCrashWindow = 3;       // 熔断窗口内最大崩溃次数
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);
    private const int ConsoleCapacity = 500;

    private readonly WorkspaceService _workspace;
    private readonly JavaRuntimeManager _java;
    private readonly string _runDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ConsoleLine> _console = [];
    private readonly object _lock = new();
    private int _crashCount;
    private DateTime _crashWindowStart;
    private Process? _process;

    public string Name { get; }
    public InstanceConfig Config { get; private set; }
    public GameTemplate Template { get; }
    public InstanceState State { get; private set; } = InstanceState.Stopped;

    public event EventHandler<InstanceStateChangedEventArgs>? StateChanged;
    public event EventHandler<ConsoleLine>? OutputReceived;

    public ServerInstance(WorkspaceService workspace, JavaRuntimeManager java, InstanceConfig config, GameTemplate template)
    {
        _workspace = workspace;
        _java = java;
        Name = config.Name;
        Config = config;
        Template = template;
        _runDir = workspace.GetRunDir(config.Name);
    }

    public IReadOnlyList<ConsoleLine> ConsoleTail() { lock (_lock) return _console.ToArray(); }

    /// <summary>启动服务器：解析 Java → 首次运行写文件 → 建进程（进程级环境）→ 管道</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_process is not null && !_process.HasExited)
                return;
        }

        // 解析 Java 可执行文件：实例 JavaPath > 实例 JavaVersion > 模板 JavaVersion
        string javaExe;
        if (Config.JavaPath is { Length: > 0 })
            javaExe = Config.JavaPath;
        else
        {
            var version = Config.JavaVersion ?? Template.JavaVersion
                ?? throw new InvalidOperationException("模板未声明 Java 版本");
            javaExe = await _java.EnsureJavaAsync(version, ct: ct);
        }

        Directory.CreateDirectory(_runDir);

        // 首次运行：写入模板 firstRunFiles（eula 同意、server.properties 端口）
        foreach (var (file, content) in Template.FirstRunFiles)
        {
            var path = Path.Combine(_runDir, file);
            if (!File.Exists(path))
                File.WriteAllText(path, content.Replace("{port}", Config.Port.ToString()));
        }

        // jar 模式：jar 缺失则从 jarUrl 下载
        if (Template.StartMode == "jar" && Template.Jar is { } jarName)
        {
            var jarPath = Path.Combine(_runDir, jarName);
            if (!File.Exists(jarPath) && Template.JarUrl is { } url)
            {
                EmitLine($"下载 {jarName}…", isStderr: false);
                await DownloadAsync(url, jarPath, ct);
            }
        }

        var psi = BuildProcessStartInfo(javaExe);
        SetState(InstanceState.Starting);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("进程启动失败");
        _process = process;

        // 输出管道
        _ = ReadOutputAsync(process.StandardOutput, isStderr: false);
        _ = ReadOutputAsync(process.StandardError, isStderr: true);

        // 退出监视（崩溃/停止 + 自动重启熔断）
        _ = WatchExitAsync(process);
    }

    private ProcessStartInfo BuildProcessStartInfo(string javaExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            WorkingDirectory = _runDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        // 合并进程级环境：实例 > 模板默认（进 ProcessStartInfo.Environment，只影响目标进程）
        foreach (var (k, v) in Template.Env)
            psi.Environment[k] = v;
        foreach (var (k, v) in Config.Env)
            psi.Environment[k] = v;
        JavaRuntimeManager.ApplyJavaEnv(psi, javaExe);

        // 启动参数：jar 模式替换变量；script/command 模式走对应启动方式
        if (Template.StartMode == "jar")
        {
            var memory = Config.MemoryMb ?? Math.Clamp(Template.MemoryMaxMb / 2, Template.MemoryMinMb, Template.MemoryMaxMb);
            var args = Template.LaunchArgs
                .Replace("{java}", $"\"{javaExe}\"")
                .Replace("{memory}", memory.ToString())
                .Replace("{jar}", $"\"{Template.Jar}\"")
                .Replace("{port}", Config.Port.ToString());
            psi.ArgumentList.Add(args);   // 单个参数串（java 命令行按空格拆，jar 模式参数简单）
            // 实际 java 调用：java -Xmx.. -jar jar nogui —— 用 ArgumentList 拆更稳：
            psi.ArgumentList.Clear();
            foreach (var part in SplitArgs(args))
                psi.ArgumentList.Add(part);
        }
        else if (Template.StartMode == "script")
        {
            psi.FileName = Path.Combine(_runDir, Template.Script ?? "run.bat");
            psi.UseShellExecute = false;
        }
        else if (Template.StartMode == "command")
        {
            psi.FileName = (Template.Command ?? "").Split(' ')[0];
            foreach (var part in SplitArgs(Template.Command ?? "").Skip(1))
                psi.ArgumentList.Add(part);
        }
        return psi;
    }

    private async Task ReadOutputAsync(StreamReader reader, bool isStderr)
    {
        try
        {
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
                EmitLine(line, isStderr);
        }
        catch { /* 进程结束流关闭 */ }
    }

    private async Task WatchExitAsync(Process process)
    {
        await process.WaitForExitAsync(_cts.Token);
        SetState(InstanceState.Stopped);

        // 自动重启 + 崩溃熔断
        if (Config.AutoRestart && !_cts.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (now - _crashWindowStart > CrashWindow)
            {
                _crashWindowStart = now;
                _crashCount = 0;
            }
            _crashCount++;
            if (_crashCount <= MaxCrashWindow)
            {
                EmitLine($"进程退出（exit={process.ExitCode}），{2 * _crashCount}s 后自动重启…", isStderr: true);
                await Task.Delay(TimeSpan.FromSeconds(2 * _crashCount), _cts.Token);
                _ = StartAsync(_cts.Token);
            }
            else
            {
                EmitLine($"短时间崩溃 {_crashCount} 次，停止自动重启（防循环）", isStderr: true);
                SetState(InstanceState.Crashed);
            }
        }
        else
        {
            SetState(process.ExitCode == 0 ? InstanceState.Stopped : InstanceState.Crashed);
        }
    }

    /// <summary>优雅停止：先发模板停止指令，超时强杀</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        var process = _process;
        if (process is null || process.HasExited)
            return;
        SetState(InstanceState.Stopping);
        try
        {
            if (!string.IsNullOrEmpty(Template.StopCommand))
            {
                await process.StandardInput.WriteLineAsync(Template.StopCommand);
                await process.StandardInput.FlushAsync();
            }
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(timeout ?? TimeSpan.FromSeconds(15)));
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>发送控制台指令（stdin）</summary>
    public async Task SendCommandAsync(string command)
    {
        var process = _process;
        if (process is null || process.HasExited)
            return;
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
    }

    private void EmitLine(string text, bool isStderr)
    {
        var line = new ConsoleLine(DateTime.Now, text, isStderr);
        lock (_lock)
        {
            _console.Add(line);
            if (_console.Count > ConsoleCapacity)
                _console.RemoveAt(0);
        }
        OutputReceived?.Invoke(this, line);
    }

    private void SetState(InstanceState state, string? message = null)
    {
        State = state;
        StateChanged?.Invoke(this, new InstanceStateChangedEventArgs(state, message));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await StopAsync(TimeSpan.FromSeconds(3));
        _process?.Dispose();
        _cts.Dispose();
    }

    private static IEnumerable<string> SplitArgs(string args)
    {
        // 简单空格拆分（带引号参数保持为一个）
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var c in args)
        {
            if (c == '"') inQuote = !inQuote;
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var bytes = await client.GetByteArrayAsync(url, ct);
        await File.WriteAllBytesAsync(destPath, bytes, ct);
    }
}
