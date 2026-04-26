using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EtlTool.App.Auth;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EtlTool.App.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly UserAuthService _auth;
    private readonly IAuditLogger _audit;

    public LoginModel(UserAuthService auth, IAuditLogger audit)
    {
        _auth = auth;
        _audit = audit;
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
        if (!ModelState.IsValid)
        {
            Error = "請輸入帳號與密碼";
            return Page();
        }

        if (!_auth.Validate(Username, Password))
        {
            Error = "帳號或密碼錯誤";
            await _audit.LogAsync(
                AuditCategory.Auth, AuditAction.LoginFailed,
                $"登入失敗：{Username}",
                actor: Username,
                severity: AuditSeverity.Warning);
            // 故意延遲 ~300ms 抵抗 timing 與暴力破解
            await Task.Delay(300);
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, Username),
            new(ClaimTypes.NameIdentifier, Username),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var props = new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(_auth.CurrentOptions.SessionHours),
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Login, $"使用者「{Username}」登入", actor: Username);

        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl()
    {
        if (string.IsNullOrWhiteSpace(ReturnUrl)) return "/";
        if (Url.IsLocalUrl(ReturnUrl)) return ReturnUrl;
        return "/";
    }
}
