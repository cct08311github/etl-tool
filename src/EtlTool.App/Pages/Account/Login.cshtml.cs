using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EtlTool.App.Auth;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EtlTool.App.Pages.Account;

// Skip auto antiforgery validation — we validate manually in OnPostAsync so we
// can return a friendly form-level error instead of the framework's bare 400
// "A valid antiforgery token was not provided" page (which silently eats the
// user's typed credentials and is a UX disaster on stale tabs / key rotation).
// Security note: still validates manually + cookies are SameSite=Lax, so login
// CSRF surface is unchanged. We only changed the error path, not the gate.
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class LoginModel : PageModel
{
    private readonly UserAuthService _auth;
    private readonly IAuditLogger _audit;
    private readonly LoginLockoutService _lockout;
    private readonly IAntiforgery _antiforgery;

    public LoginModel(UserAuthService auth, IAuditLogger audit, LoginLockoutService lockout, IAntiforgery antiforgery)
    {
        _auth = auth;
        _audit = audit;
        _lockout = lockout;
        _antiforgery = antiforgery;
    }

    [BindProperty, Required] public string Username { get; set; } = "";
    [BindProperty, Required] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(SafeReturnUrl());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Manual antiforgery validation — see [IgnoreAntiforgeryToken] note above.
        // 常見失敗成因：登入頁 tab 開太久（token 過期）、伺服器重啟 → data-protection
        // key 更換、瀏覽器 disabled cookies、跨子網域 cookie 衝突。
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            await _audit.LogAsync(
                AuditCategory.Auth, AuditAction.LoginFailed,
                $"登入頁 antiforgery 驗證失敗（很可能是 tab 開太久或伺服器重啟）：{ex.Message}",
                actor: string.IsNullOrWhiteSpace(Username) ? "anonymous" : Username,
                severity: AuditSeverity.Warning);
            Error = "登入頁面已過期或瀏覽器 cookie 異常。請重新整理（F5）後再試一次。";
            // 把密碼清掉避免顯示 — 帳號保留方便重打
            Password = "";
            return Page();
        }

        if (!ModelState.IsValid)
        {
            Error = "請輸入帳號與密碼";
            return Page();
        }

        // 先檢查 lockout：避免被鎖中的帳號還能繼續嘗試（防止計數重啟）
        var lockedSecs = _lockout.GetLockedSeconds(Username);
        if (lockedSecs > 0)
        {
            Error = $"連續登入失敗次數過多，帳號已被鎖定。請於 {lockedSecs} 秒後再試。";
            await Task.Delay(300);
            return Page();
        }

        var validatedUser = await _auth.ValidateAsync(Username, Password);
        if (validatedUser is null)
        {
            var (fails, justLocked) = _lockout.RecordFailure(Username);

            if (justLocked)
            {
                await _audit.LogAsync(
                    AuditCategory.Auth, AuditAction.LoginFailed,
                    $"帳號「{Username}」連續 {fails} 次失敗已被鎖定 15 分鐘",
                    actor: Username,
                    severity: AuditSeverity.Error);
                Error = "連續登入失敗次數過多，帳號已被鎖定 15 分鐘。";
            }
            else
            {
                await _audit.LogAsync(
                    AuditCategory.Auth, AuditAction.LoginFailed,
                    $"登入失敗：{Username}（第 {fails} 次）",
                    actor: Username,
                    severity: AuditSeverity.Warning);
                Error = "帳號或密碼錯誤";
            }
            await Task.Delay(300);
            return Page();
        }

        // 成功 → 清空 lockout 計數
        _lockout.RecordSuccess(Username);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, validatedUser.Username),
            new(ClaimTypes.NameIdentifier, validatedUser.Id.ToString()),
            new(ClaimTypes.Role, validatedUser.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        // 用新的 ResolveTimeoutMinutes()（取代被棄用的 SessionHours）
        var timeoutMinutes = _auth.CurrentOptions.ResolveTimeoutMinutes();
        var props = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(timeoutMinutes),
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Login,
            $"使用者「{validatedUser.Username}」({validatedUser.Role}) 登入",
            actor: validatedUser.Username);

        // 銀行控制：若 user 被標記必須變更密碼（首次登入或 admin 重設後，
        // 或達 MaxPasswordAgeDays 過期）→ 強制導向 ChangePassword 頁
        var mustChange = validatedUser.MustChangePassword
            || IsPasswordExpired(validatedUser);
        if (mustChange)
        {
            return Redirect("/Account/ChangePassword?returnUrl=" + Uri.EscapeDataString(SafeReturnUrl()));
        }

        return LocalRedirect(SafeReturnUrl());
    }

    private bool IsPasswordExpired(User u)
    {
        var maxAge = _auth.CurrentOptions.MaxPasswordAgeDays;
        if (maxAge <= 0) return false;
        return DateTime.UtcNow - u.LastPasswordChangedAt > TimeSpan.FromDays(maxAge);
    }

    private string SafeReturnUrl()
    {
        if (string.IsNullOrWhiteSpace(ReturnUrl)) return "/";
        if (Url.IsLocalUrl(ReturnUrl)) return ReturnUrl;
        return "/";
    }
}
