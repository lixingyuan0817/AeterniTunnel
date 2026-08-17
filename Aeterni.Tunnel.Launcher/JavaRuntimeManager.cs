using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aeterni.Tunnel.Launcher;

/// <summary>
/// Java 运行时管理：JDK 检测（系统/缓存）、按版本下载（Azul Zulu）、进程级环境注入。
/// 缓存目录：runtime/java/zulu-&lt;版本&gt;/（多实例共享，不重复下载）。
/// </summary>
public sealed class JavaRuntimeManager
{
    private readonly string _javaDir;

    public JavaRuntimeManager(WorkspaceService workspace)
    {
        _javaDir = Path.Combine(workspace.RuntimeDir, "java");
    }

    public string GetJdkDir(string version) => Path.Combine(_javaDir, $"zulu-{version}");

    private static string JavaExeName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "java.exe" : "java";

    /// <summary>某版本 JDK 是否已安装（bin/java 存在）</summary>
    public bool IsInstalled(string version)
    {
        var dir = GetJdkDir(version);
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "bin", JavaExeName()));
    }

    /// <summary>已安装版本列表</summary>
    public IEnumerable<string> ListInstalledVersions() =>
        Directory.Exists(_javaDir)
            ? Directory.GetDirectories(_javaDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith("zulu-"))
                .Select(n => n!["zulu-".Length..])!
            : [];

    /// <summary>解析某版本 Java 可执行文件（未安装返回 null）</summary>
    public string? FindJavaExe(string version) =>
        IsInstalled(version) ? Path.Combine(GetJdkDir(version), "bin", JavaExeName()) : null;

    /// <summary>确保某版本 JDK 可用：已缓存直接返回，否则从 Azul 下载</summary>
    public async Task<string> EnsureJavaAsync(string version, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (FindJavaExe(version) is { } existing)
            return existing;

        progress?.Report($"下载 JDK {version}（Zulu）…");
        var url = await ResolveDownloadUrlAsync(version, ct);
        if (url is null)
            throw new InvalidOperationException($"Azul API 未找到 JDK {version} 的下载链接");

        var zipPath = Path.Combine(Path.GetTempPath(), $"zulu-{version}-{Guid.NewGuid():N}.zip");
        var extractPath = Path.Combine(Path.GetTempPath(), $"zulu-{version}-{Guid.NewGuid():N}");
        try
        {
            await DownloadAsync(url, zipPath, progress, ct);
            progress?.Report("解压 JDK…");
            ZipFile.ExtractToDirectory(zipPath, extractPath);

            // 解压后找 JDK 根（含 bin/java）：顶层可能套一层目录
            var jdkRoot = FindJdkRoot(extractPath);
            if (jdkRoot is null)
                throw new InvalidOperationException("解压内容未找到 JDK 根目录");

            var target = GetJdkDir(version);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(jdkRoot, target);
            progress?.Report($"JDK {version} 安装完成");
            return Path.Combine(target, "bin", JavaExeName());
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(extractPath, recursive: true); } catch { }
        }
    }

    private static string? FindJdkRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "bin", JavaExeName())))
            return dir;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "bin", JavaExeName())))
                return sub;
        }
        return null;
    }

    private sealed record AzulPackage(
        [property: JsonPropertyName("download_url")] string? DownloadUrl,
        [property: JsonPropertyName("size")] long? Size);

    private static async Task<string?> ResolveDownloadUrlAsync(string version, CancellationToken ct)
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "macos";
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        var url = "https://api.azul.com/metadata/v1/zulu/packages/?" +
                  $"java_version={version}&os={os}&arch={arch}&archive_type=zip" +
                  "&java_package_type=jdk&latest=true&release_status=ga&certification_status=ca";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = await client.GetStringAsync(url, ct);
        var packages = JsonSerializer.Deserialize<List<AzulPackage>>(json);
        return packages?.FirstOrDefault(p => !string.IsNullOrEmpty(p.DownloadUrl))?.DownloadUrl;
    }

    private static async Task DownloadAsync(string url, string destPath, IProgress<string>? progress, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
                progress?.Report($"下载 {(int)(read * 100 / total)}%（{read / 1048576}/{total / 1048576} MB）");
        }
    }

    /// <summary>进程级环境注入：JAVA_HOME + PATH 前置 JDK bin（只影响目标进程，不动全局）</summary>
    public static void ApplyJavaEnv(System.Diagnostics.ProcessStartInfo psi, string javaExe)
    {
        var home = Path.GetDirectoryName(Path.GetDirectoryName(javaExe)) ?? "";
        var bin = Path.Combine(home, "bin");
        psi.Environment["JAVA_HOME"] = home;
        var path = psi.Environment.TryGetValue("PATH", out var p) ? p : "";
        psi.Environment["PATH"] = bin + Path.PathSeparator + path;
    }
}
