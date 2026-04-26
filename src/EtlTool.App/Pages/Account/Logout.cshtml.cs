using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EtlTool.App.Pages.Account;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly IAuditLogger _audit;
    public LogoutModel(IAuditLogger audit) { _audit = audit; }

    public async Task<IActionResult> OnGetAsync()
    {
        return await SignOutAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        return await SignOutAsync();
    }

    private async Task<IActionResult> SignOutAsync()
    {
        var name = User.Identity?.Name;
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!string.IsNullOrEmpty(name))
        {
            await _audit.LogAsync(AuditCategory.Auth, AuditAction.Logout,
                $"使用者「{name}」登出", actor: name);
        }
        return Redirect("/Account/Login");
    }
}
