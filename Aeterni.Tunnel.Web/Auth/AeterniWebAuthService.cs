using System.Security.Cryptography;
using System.Text;
using Aeterni.Tunnel.Engine.Config;
using Microsoft.Extensions.Logging;

namespace Aeterni.Tunnel.Web.Auth;

/// <summary>
/// Aeterni Web 登录认证服务：管理 webToken 的校验（加盐 SHA256，常量时间比较）。
/// Token 来源优先级：AETERNI_WEB_TOKEN 环境变量 &gt; server.toml（webToken+webTokenSalt 哈希落盘）
/// &gt; 配置 Aeterni:WebToken（开发便利）&gt; 首启随机生成并写回 server.toml。
/// 与 ATS token（ServerConfig.Token，ATC 认证）分层独立。
/// 支持运行时修改（ChangeToken，当前进程生效；file 来源会写回 server.toml 持久化）。
/// </summary>
public sealed class AeterniWebAuthService
{
    public const string EnvSource = "env";
    public const string FileSource = "file";
    public const string ConfigSource = "config";
    public const string GeneratedSource = "generated";

    private readonly object _lock = new();
    private readonly string? _filePath;
    private readonly ILogger? _logger;
    private byte[] _salt;
    private byte[] _hash;

    public AeterniWebAuthService(string source, byte[] salt, byte[] hash, string? filePath = null, ILogger? logger = null)
    {
        Source = source;
        _salt = salt;
        _hash = hash;
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>token 来源（env / file / config / generated）；环境变量来源优先级最高，不可在运行时修改</summary>
    public string Source { get; }

    /// <summary>运行时是否可修改 token（环境变量来源不可修改）</summary>
    public bool CanChange => Source != EnvSource;

    /// <summary>生成随机明文 token + 盐 + 哈希（--reset-token / 首启用）</summary>
    public static (string Plain, byte[] Salt, byte[] Hash) Generate()
    {
        var plain = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var salt = RandomNumberGenerator.GetBytes(16);
        return (plain, salt, Hash(plain, salt));
    }

    /// <summary>加盐 SHA256（十六进制输出/比对用）</summary>
    public static byte[] Hash(string token, byte[] salt)
        => SHA256.HashData(Encoding.UTF8.GetBytes(token).Concat(salt).ToArray());

    /// <summary>校验登录 token（常量时间比较，防时序攻击）</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        lock (_lock)
            return CryptographicOperations.FixedTimeEquals(_hash, Hash(token, _salt));
    }

    /// <summary>
    /// 修改 webToken：校验旧 token 后重新生成盐与哈希（当前进程立即生效）。
    /// file 来源写回 server.toml 持久化（webToken + webTokenSalt）。
    /// 返回 false 表示旧 token 错误、新 token 为空或来源为环境变量（不可修改）。
    /// </summary>
    public bool ChangeToken(string? oldToken, string? newToken)
    {
        if (Source == EnvSource || string.IsNullOrEmpty(newToken))
            return false;

        lock (_lock)
        {
            if (string.IsNullOrEmpty(oldToken) ||
                !CryptographicOperations.FixedTimeEquals(_hash, Hash(oldToken, _salt)))
                return false;

            _salt = RandomNumberGenerator.GetBytes(16);
            _hash = Hash(newToken, _salt);
        }

        if (Source == FileSource && _filePath is not null)
            PersistToFile();
        return true;
    }

    private void PersistToFile()
    {
        try
        {
            var cfg = ConfigLoader.Load(_filePath!) ?? new ServerConfig();
            cfg.WebToken = Convert.ToHexString(_hash);
            cfg.WebTokenSalt = Convert.ToHexString(_salt);
            ConfigLoader.SaveServer(_filePath!, cfg);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "webToken 写回 {Path} 失败（当前进程内仍生效）", _filePath);
        }
    }
}
