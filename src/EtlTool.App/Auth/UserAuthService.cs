using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EtlTool.App.Auth;

/// <summary>單一使用者的密碼驗證。Singleton — 不持有任何 per-request 狀態。</summary>
public sealed class UserAuthService
{
    public const string DefaultDevPassword = "etladmin";

    private readonly IOptionsMonitor<AuthOptions> _opts;
    private readonly ILogger<UserAuthService> _log;

    public UserAuthService(IOptionsMonitor<AuthOptions> opts, ILogger<UserAuthService> log)
    {
        _opts = opts;
        _log = log;

        var current = _opts.CurrentValue;
        if (string.IsNullOrEmpty(current.PasswordHash) && string.IsNullOrEmpty(current.Password))
        {
            _log.LogWarning(
                "[SECURITY] Auth:PasswordHash 和 Auth:Password 都未設定，使用內建預設密碼 '{Default}' — 請勿用於正式環境，應在 appsettings 設 Auth:PasswordHash (BCrypt)。",
                DefaultDevPassword);
        }
        else if (string.IsNullOrEmpty(current.PasswordHash))
        {
            _log.LogWarning("[SECURITY] Auth:Password 為純文字，建議改用 Auth:PasswordHash (BCrypt) 以避免 appsettings 外洩風險。");
        }
    }

    public AuthOptions CurrentOptions => _opts.CurrentValue;

    public bool Validate(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || password is null) return false;

        var opts = _opts.CurrentValue;
        if (!string.Equals(username, opts.Username, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrEmpty(opts.PasswordHash))
        {
            try { return BCrypt.Net.BCrypt.Verify(password, opts.PasswordHash); }
            catch (Exception ex)
            {
                _log.LogError(ex, "BCrypt verify failed; check Auth:PasswordHash format.");
                return false;
            }
        }

        if (!string.IsNullOrEmpty(opts.Password))
            return ConstantTimeEquals(password, opts.Password);

        return ConstantTimeEquals(password, DefaultDevPassword);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    /// <summary>給管理員生成 BCrypt 雜湊用，方便寫進 appsettings.Production.json。</summary>
    public static string Hash(string plaintext) => BCrypt.Net.BCrypt.HashPassword(plaintext, workFactor: 12);
}
