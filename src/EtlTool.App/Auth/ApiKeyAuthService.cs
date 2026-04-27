using System.Security.Cryptography;
using System.Text;

namespace EtlTool.App.Auth;

/// <summary>
/// 從 Api:Keys[] config 讀允許的 API key 清單。空 = 不啟用 API key auth
/// （/api/* 走 IP allowlist；適合純內網部署）。
///
/// IsValid 用 constant-time compare 避免 timing attack — 對短字串其實很弱
/// 但好過 string == 一翻兩瞪眼。
/// </summary>
public sealed class ApiKeyAuthService
{
    private readonly List<byte[]> _hashedKeys;
    public bool IsEnabled { get; }

    public ApiKeyAuthService(IConfiguration config)
        : this(config.GetSection("Api:Keys").Get<string[]>() ?? Array.Empty<string>())
    { }

    public ApiKeyAuthService(IEnumerable<string> rawKeys)
    {
        var keys = rawKeys.Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _hashedKeys = keys.Select(HashKey).ToList();
        IsEnabled = _hashedKeys.Count > 0;
    }

    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var hash = HashKey(candidate.Trim());
        // Iterate ALL keys regardless of match to prevent timing leak per-key
        bool match = false;
        foreach (var k in _hashedKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, k)) match = true;
        }
        return match;
    }

    private static byte[] HashKey(string key)
        => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
