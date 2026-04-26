using System.Security.Cryptography;
using System.Text;
using EtlTool.Core.Models;
using EtlTool.Data.Repositories;
using Microsoft.Extensions.Options;

namespace EtlTool.App.Auth;

/// <summary>
/// 帳號密碼驗證 (singleton)。
///
/// 優先序：
///   1) Users 資料表非空 → 用 UserRepository 驗證 + 取 role；config 失效
///   2) Users 資料表空 → 用 appsettings Auth:Username/Password/PasswordHash 驗證
///      （首次部署 / 緊急 break-glass 用，會 log 警告）
/// </summary>
public sealed class UserAuthService
{
    public const string DefaultDevPassword = "etladmin";

    private readonly IOptionsMonitor<AuthOptions> _opts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserAuthService> _log;

    public UserAuthService(IOptionsMonitor<AuthOptions> opts, IServiceScopeFactory scopeFactory, ILogger<UserAuthService> log)
    {
        _opts = opts;
        _scopeFactory = scopeFactory;
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

    /// <summary>
    /// 回傳驗證後的 User（若用 DB）或合成的 Admin user（若 fallback 到 config）。
    /// 失敗回 null。
    /// </summary>
    public async Task<User?> ValidateAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(username) || password is null) return null;

        // 1) 優先：DB
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<UserRepository>();
        var count = await repo.CountAsync(ct);

        if (count > 0)
        {
            var user = await repo.FindByUsernameAsync(username, ct);
            if (user is null) return null;
            if (!user.IsActive) return null;
            try
            {
                if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                {
                    await repo.UpdateLastLoginAsync(user.Id, ct);
                    return user;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BCrypt verify failed for user {Username}", username);
            }
            return null;
        }

        // 2) Fallback: config（Users 表空）
        _log.LogWarning("[SECURITY] Users 資料表為空，使用 appsettings Auth 驗證（fallback 模式）");
        var opts = _opts.CurrentValue;
        if (!string.Equals(username, opts.Username, StringComparison.Ordinal)) return null;

        bool ok;
        if (!string.IsNullOrEmpty(opts.PasswordHash))
        {
            try { ok = BCrypt.Net.BCrypt.Verify(password, opts.PasswordHash); }
            catch { ok = false; }
        }
        else if (!string.IsNullOrEmpty(opts.Password))
            ok = ConstantTimeEquals(password, opts.Password);
        else
            ok = ConstantTimeEquals(password, DefaultDevPassword);

        if (!ok) return null;

        // 合成 Admin user（不存 DB；StartupBootstrapper 會在啟動時 seed 一次）
        return new User
        {
            Id = Guid.Empty,
            Username = opts.Username,
            Role = UserRole.Admin,
            IsActive = true,
        };
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
