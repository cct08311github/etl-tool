namespace EtlTool.App.Services;

/// <summary>
/// 銀行/企業常見的安全 response headers。對應 OWASP Secure Headers Project 與多數合規檢核要求。
///
/// 套用：
///   X-Content-Type-Options: nosniff           — 禁 MIME sniffing
///   X-Frame-Options: DENY                     — 禁被 iframe 嵌入（防 clickjacking）
///   Referrer-Policy: strict-origin-when-cross-origin — 跨站不外洩 path
///   Permissions-Policy: 全部關閉              — 不需要 camera/mic/geo 等
///   Cross-Origin-Opener-Policy: same-origin   — process isolation
///   Cross-Origin-Resource-Policy: same-origin — 跨站資源拉取防護
///   Strict-Transport-Security: 6 個月 (僅 HTTPS 連線下發)
///   Content-Security-Policy: 透過參數客製（Blazor Server 對 CSP 較難全鎖）
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // 防止 response 已 start 後仍嘗試寫 header（會 throw）
        context.Response.OnStarting(() =>
        {
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            // HSTS 只在 HTTPS 下發送（避免 HTTP 開發環境誤鎖）
            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=15552000; includeSubDomains";
            }

            if (!string.IsNullOrEmpty(_options.ContentSecurityPolicy))
            {
                headers["Content-Security-Policy"] = _options.ContentSecurityPolicy;
            }

            // 移除洩漏資訊的 default headers
            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            return Task.CompletedTask;
        });

        await _next(context);
    }
}

public sealed class SecurityHeadersOptions
{
    /// <summary>
    /// CSP（可選）。Blazor Server 需要 'unsafe-inline' 給內嵌樣式與連線到 SignalR。
    /// 若為 null/空字串則不送 CSP（漸進式採用，避免破壞既有頁面）。
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }
}

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(
        this IApplicationBuilder app, SecurityHeadersOptions? options = null)
        => app.UseMiddleware<SecurityHeadersMiddleware>(options ?? new SecurityHeadersOptions());
}
