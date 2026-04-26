using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EtlTool.App.Auth;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EtlTool.App.Pages.Account;

/// <summary>
/// /Account/ChangePassword — 已登入使用者自行變更密碼。
/// 也是 MustChangePassword=true 時 Login 強制導向的目標頁。
/// </summary>
[Authorize] // 必須已登入
public class ChangePasswordModel : PageModel
{
    private readonly UserAuthService _auth;
    private readonly UserRepository _userRepo;
    private readonly IAuditLogger _audit;

    public ChangePasswordModel(UserAuthService auth, UserRepository userRepo, IAuditLogger audit)
    {
        _auth = auth;
        _userRepo = userRepo;
        _audit = audit;
    }

    [BindProperty, Required, MinLength(1)] public string OldPassword { get; set; } = "";
    [BindProperty, Required, MinLength(12)] public string NewPassword { get; set; } = "";
    [BindProperty, Required] public string ConfirmPassword { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; set; }
    public string? Success { get; set; }
    public bool Forced { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return Redirect("/Account/Login");

        // 查 user 是否被標記為 MustChangePassword（顯示「強制」提示文字）
        var u = await _userRepo.FindByUsernameAsync(username, HttpContext.RequestAborted);
        Forced = u?.MustChangePassword ?? false;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return Redirect("/Account/Login");

        if (!ModelState.IsValid)
        {
            Error = "請完整填寫表單，新密碼至少 12 字元。";
            await PopulateForcedAsync(username);
            return Page();
        }
        if (NewPassword != ConfirmPassword)
        {
            Error = "新密碼與確認密碼不相同。";
            await PopulateForcedAsync(username);
            return Page();
        }
        if (NewPassword == OldPassword)
        {
            Error = "新密碼不可與舊密碼相同。";
            await PopulateForcedAsync(username);
            return Page();
        }

        // 1) 用 UserAuthService 驗舊密碼（同時也順便確認帳號目前是 active）
        var validated = await _auth.ValidateAsync(username, OldPassword, HttpContext.RequestAborted);
        if (validated is null)
        {
            await _audit.LogAsync(AuditCategory.Auth, AuditAction.LoginFailed,
                $"變更密碼失敗：舊密碼錯誤（user={username}）",
                actor: username, severity: AuditSeverity.Warning,
                ct: HttpContext.RequestAborted);
            Error = "舊密碼錯誤";
            await Task.Delay(300);
            await PopulateForcedAsync(username);
            return Page();
        }

        // 2) 強度檢查
        var eval = PasswordPolicy.Evaluate(NewPassword, username);
        if (!eval.IsStrong)
        {
            Error = eval.Reason ?? "密碼強度不足";
            await PopulateForcedAsync(username);
            return Page();
        }

        // 3) DB 路徑下做 reuse check + write
        if (validated.Id == Guid.Empty)
        {
            // Config fallback user — 不能透過此頁改密碼（要改 appsettings）
            Error = "目前以 appsettings fallback 帳號登入，請改 appsettings 的 Auth:PasswordHash。";
            await PopulateForcedAsync(username);
            return Page();
        }

        var ok_err = await _userRepo.ChangeOwnPasswordWithReuseCheckAsync(
            validated.Id,
            NewPassword,
            hasher: pw => UserAuthService.Hash(pw),
            verifier: (pw, hash) => BCrypt.Net.BCrypt.Verify(pw, hash),
            actor: username,
            ct: HttpContext.RequestAborted);
        var ok = ok_err.Success;
        var err = ok_err.Error;
        if (!ok)
        {
            Error = err ?? "變更失敗";
            await PopulateForcedAsync(username);
            return Page();
        }

        Success = "密碼已成功變更，將重新登入。";

        // 為了讓新的 cookie reflect MustChangePassword=false，重新 sign-in
        // (簡單起見：直接 sign-out 強迫使用者用新密碼再 login)
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/Account/Login");
    }

    private async Task PopulateForcedAsync(string username)
    {
        var u = await _userRepo.FindByUsernameAsync(username, HttpContext.RequestAborted);
        Forced = u?.MustChangePassword ?? false;
    }

}
