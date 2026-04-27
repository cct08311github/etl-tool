using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Auth;

/// <summary>
/// 銀行合規閘門：強制所有請求走 HTTPS。
///
/// 啟用條件：appsettings 設 <c>Security:RequireHttps = true</c>。
/// 預設關閉（dev 多走 HTTP），但 prod 部署應強制開啟。
///
/// 判定邏輯：
///   1. <see cref="HttpRequest.IsHttps"/> = true → 通過
///   2. <c>X-Forwarded-Proto = https</c>（反向代理場景，IIS / nginx 已 TLS 終端）→ 通過
///   3. 其他 → 503 + 簡短 HTML 解釋（不是 401/403 — 這是基礎建設層問題，不是授權問題）
///
/// 設計權衡：
///   - 為什麼不用 ASP.NET Core 內建 <c>UseHttpsRedirection</c>？
///     那會 302 redirect 到 https://同一個 host，但反向代理場景下 host 可能不對；
///     而且 banking 環境我們不要「自動 redirect」— 我們要「明確擋下並寫 audit」。
///   - 為什麼不用 <c>RequireHttpsAttribute</c>？
///     那是 MVC filter，跑得太晚；middleware 一律走前面，包含靜態檔。
///   - <c>/healthz</c> 仍允許 HTTP（LB / firewall 可能用 plain HTTP probe；
///     但 detailed health 與 /api/* 都受擋）。
///
/// Audit：每次擋下都寫一筆 Warning，一段時間後 admin 可以從 /logs 找出
/// 「誰還在用 HTTP 連我們」清單，視需要關掉這個 gate（測試）或處理該客戶端。
/// </summary>
public sealed class RequireHttpsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _enabled;

    // 即使啟用 RequireHttps，仍允許這些路徑走 HTTP（LB health probe）
    private static readonly string[] HttpAllowedPaths = { "/healthz" };

    public RequireHttpsMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _enabled = config.GetValue<bool?>("Security:RequireHttps") ?? false;
    }

    public async Task InvokeAsync(HttpContext context, IAuditLogger audit)
    {
        if (!_enabled)
        {
            await _next(context);
            return;
        }

        if (IsHttpsEffective(context))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        foreach (var allowed in HttpAllowedPaths)
        {
            if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }
        }

        // 擋下 + audit（每分鐘可能很多筆，但 audit 系統有保留政策；可接受）
        await audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"⛔ 阻擋 plain HTTP 請求 — Security:RequireHttps=true，path={path}, ip={context.Connection.RemoteIpAddress}",
            severity: AuditSeverity.Warning, actor: "anonymous", ct: context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            "<!doctype html><html><body style='font-family:sans-serif;padding:2rem'>" +
            "<h2>503 — 須使用 HTTPS</h2>" +
            "<p>此服務已配置強制使用 HTTPS（<code>Security:RequireHttps=true</code>）。" +
            "請改用 <code>https://</code> 開頭的 URL 連線。</p>" +
            "<p>若您是反向代理或負載平衡器，請設定 <code>X-Forwarded-Proto: https</code> header。</p>" +
            "<p>已記入稽核日誌。</p>" +
            "</body></html>",
            context.RequestAborted);
    }

    /// <summary>
    /// 判斷請求實質上是否為 HTTPS。考慮 ASP.NET 內建的 <c>UseForwardedHeaders</c>
    /// 已套用 X-Forwarded-Proto；若 forwarded header 已被處理 → IsHttps 會反映正確值。
    /// 此處再多一層 fallback：若 Scheme 仍是 http 但有 X-Forwarded-Proto: https → 視為通過。
    /// </summary>
    private static bool IsHttpsEffective(HttpContext context)
    {
        if (context.Request.IsHttps) return true;
        var fwdProto = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
        return string.Equals(fwdProto, "https", StringComparison.OrdinalIgnoreCase);
    }
}

public static class RequireHttpsMiddlewareExtensions
{
    public static IApplicationBuilder UseRequireHttps(this IApplicationBuilder app)
        => app.UseMiddleware<RequireHttpsMiddleware>();
}
