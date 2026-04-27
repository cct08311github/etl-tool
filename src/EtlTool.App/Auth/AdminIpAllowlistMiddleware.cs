using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Auth;

/// <summary>
/// 對 admin-only 路徑進行 IP allowlist 過濾。
/// 若 Auth:AdminIpAllowlist 設了 (非空 / 非 "*")，且 user 的角色含 Admin，
/// 但來源 IP 不在 allowlist 內 → 回 403。
///
/// 不對 Operator / Viewer 套用 — 他們不能執行高風險動作，從別的 segment 進來 OK。
/// 也不對 /Account/Login / /healthz 套用 — 否則無法登入或健康檢查會壞。
/// </summary>
public sealed class AdminIpAllowlistMiddleware
{
    private static readonly string[] ProtectedPathPrefixes =
    {
        "/users",
        "/approvals",
        "/audit",                      // future audit pages
        "/scheduler",                  // scheduler inspection — diagnostic but reveals job structure
        "/system",                     // system info page — paths, sizes, webhook URL host (masked but still)
        "/Account/AuditExport",        // downloading full audit trail = sensitive
        "/Account/SqliteBackup",       // downloading entire DB = extremely sensitive
        "/Account/BackupFile",         // download / delete individual backup files
    };

    private readonly RequestDelegate _next;

    public AdminIpAllowlistMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext context, AdminIpAllowlistService allowlist, IAuditLogger audit)
    {
        if (!allowlist.IsEnabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isProtected = false;
        foreach (var prefix in ProtectedPathPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                isProtected = true;
                break;
            }
        }

        if (!isProtected)
        {
            await _next(context);
            return;
        }

        // 只對已登入且為 Admin 的 user 套用（未登入會先被 cookie auth 擋去 login）
        var user = context.User;
        if (!(user.Identity?.IsAuthenticated ?? false) || !user.IsInRole("Admin"))
        {
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (allowlist.IsAllowed(remoteIp))
        {
            await _next(context);
            return;
        }

        // 阻擋 + audit
        var actor = user.Identity?.Name;
        await audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"⛔ 阻擋 admin 連線 — 來源 IP {remoteIp} 不在 Auth:AdminIpAllowlist 內（path={path}）",
            severity: AuditSeverity.Warning, actor: actor, ct: context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            "<!doctype html><html><body style='font-family:sans-serif;padding:2rem'>" +
            "<h2>403 Forbidden</h2>" +
            $"<p>來源 IP <code>{remoteIp}</code> 不在 admin allowlist 內，無法存取此頁。</p>" +
            "<p>已記入稽核日誌。請聯絡系統管理員。</p>" +
            "</body></html>",
            context.RequestAborted);
    }
}

public static class AdminIpAllowlistMiddlewareExtensions
{
    public static IApplicationBuilder UseAdminIpAllowlist(this IApplicationBuilder app)
        => app.UseMiddleware<AdminIpAllowlistMiddleware>();
}
