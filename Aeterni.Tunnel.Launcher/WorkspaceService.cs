using Aeterni.Tunnel.Common;

namespace Aeterni.Tunnel.Launcher;

/// <summary>
/// 工作空间管理：servers/（每实例独立目录）+ runtime/（JDK 等全局缓存）。
/// 默认根：~/AeterniTunnel（可配置）。
/// </summary>
public sealed class WorkspaceService
{
    public string WorkspaceRoot { get; }

    /// <summary>实例目录根</summary>
    public string ServersDir => Path.Combine(WorkspaceRoot, "servers");

    /// <summary>运行时缓存根（JDK / SteamCMD 等全局共享）</summary>
    public string RuntimeDir => Path.Combine(WorkspaceRoot, "runtime");

    public WorkspaceService(string? root = null)
    {
        WorkspaceRoot = root
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AeterniTunnel");
    }

    /// <summary>确保目录结构存在</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ServersDir);
        Directory.CreateDirectory(RuntimeDir);
    }

    public string GetInstanceDir(string name) => Path.Combine(ServersDir, name);

    /// <summary>进程工作目录（run/，游戏实际文件放这）</summary>
    public string GetRunDir(string name) => Path.Combine(GetInstanceDir(name), "run");

    public string GetConfigPath(string name) => Path.Combine(GetInstanceDir(name), "server.toml");

    /// <summary>实例名是否已存在（唯一校验）</summary>
    public bool Exists(string name) => Directory.Exists(GetInstanceDir(name));

    /// <summary>列出所有实例名（含 server.toml 的目录）</summary>
    public IEnumerable<string> ListInstances()
    {
        if (!Directory.Exists(ServersDir))
            return [];
        return Directory.GetDirectories(ServersDir)
            .Where(d => File.Exists(Path.Combine(d, "server.toml")))
            .Select(Path.GetFileName)!;
    }

    /// <summary>创建实例工作目录（含 run/）</summary>
    public string CreateInstance(string name)
    {
        if (Exists(name))
            throw new InvalidOperationException($"实例「{name}」已存在");
        Directory.CreateDirectory(GetRunDir(name));
        return GetInstanceDir(name);
    }

    /// <summary>删除实例（整个目录，含存档）</summary>
    public void DeleteInstance(string name)
    {
        var dir = GetInstanceDir(name);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
