using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Auth;

/// <summary>
/// 對 /api/* 路徑檢查 X-Api-Key header（若 Api:Keys 已設定）。
/// 失敗回 401 + 簡短 JSON。成功 → 繼續往下流。
///
/// 與 AdminIpAllowlistMiddleware 是 AND 關係：
///   - 若 IP allowlist 啟用且 IP 不在內 → 已在前面 middleware 擋住
///   - 若 API key 啟用且 header 缺/錯 → 在這裡擋
///   - 兩個都不啟用 → 純信任內網，/api/* 任何人都可讀
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext context, ApiKeyAuthService keys, IAuditLogger audit)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!keys.IsEnabled)
        {
            // No keys configured → API key check disabled. Caller still needs to pass
            // the IP allowlist if it's enabled (handled by AdminIpAllowlistMiddleware).
            await _next(context);
            return;
        }

        var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided))
        {
            // Also accept Authorization: Bearer <key> for tooling friendliness
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = authHeader["Bearer ".Length..].Trim();
            }
        }

        if (!keys.IsValid(provided))
        {
            // Audit failed authentication attempts (low severity; rate-limited by repetition)
            await audit.LogAsync(AuditCategory.Auth, AuditAction.LoginFailed,
                $"⛔ /api/ 請求 API key 無效或缺失，path={path}, ip={context.Connection.RemoteIpAddress}",
                severity: AuditSeverity.Warning, actor: "anonymous", ct: context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                """{"error":"missing or invalid X-Api-Key header"}""",
                context.RequestAborted);
            return;
        }

        await _next(context);
    }
}

public static class ApiKeyAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
        => app.UseMiddleware<ApiKeyAuthMiddleware>();
}
