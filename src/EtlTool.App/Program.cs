using EtlTool.App.Auth;
using EtlTool.App.Components;
using EtlTool.App.Services;
using EtlTool.Connectors;
using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Scheduling;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using Quartz;
using Serilog;

// CLI 工具子指令：--hash-password 輸出 BCrypt 雜湊後直接結束（給管理員生成 PasswordHash）
if (args.Length > 0 && args[0] == "--hash-password")
{
    string pwd;
    if (Console.IsInputRedirected)
    {
        pwd = Console.In.ReadToEnd().TrimEnd('\r', '\n');
    }
    else
    {
        Console.Write("Password: ");
        pwd = ReadMaskedPassword();
        Console.WriteLine();
    }
    if (string.IsNullOrEmpty(pwd))
    {
        Console.Error.WriteLine("ERROR: empty password");
        Environment.Exit(1);
    }
    Console.WriteLine(UserAuthService.Hash(pwd));
    Environment.Exit(0);

    static string ReadMaskedPassword()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return sb.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// 解析資料目錄：預設 ContentRoot/data；可由 ETLTOOL_DATA_DIR 或 appsettings DataDirectory 覆寫
var dataDir = builder.Configuration["DataDirectory"]
              ?? Environment.GetEnvironmentVariable("ETLTOOL_DATA_DIR")
              ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "keys"));
Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

// Serilog
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(dataDir, "logs", "etltool-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

// Data Protection（連線字串加密用）— 落地到 dataDir/keys
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("EtlTool");

// EF Core SQLite
var sqlitePath = Path.Combine(dataDir, "etltool.db");
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite($"Data Source={sqlitePath}"));

// Repositories + Core abstractions
builder.Services.AddScoped<IConnectionStringProtector, DataProtectionConnectionStringProtector>();
builder.Services.AddScoped<ConnectionRepository>();
builder.Services.AddScoped<EtlTaskRepository>();
builder.Services.AddScoped<RunHistoryRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<ApprovalRepository>();
builder.Services.AddScoped<EntityChangeHistoryRepository>();
builder.Services.AddScoped<MaintenanceWindowRepository>();
builder.Services.AddScoped<RuntimeSettingsRepository>();
builder.Services.AddSingleton<EtlTool.App.Services.RuntimeSettingsService>();
builder.Services.AddScoped<IMaintenanceWindowProvider>(sp => sp.GetRequiredService<MaintenanceWindowRepository>());
builder.Services.AddScoped<AuditQueryRepository>();
builder.Services.AddScoped<AuditChainVerifier>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

builder.Services.AddScoped<EtlTool.App.Services.SourcePreviewService>();

builder.Services.AddScoped<IConnectionLookup>(sp => sp.GetRequiredService<ConnectionRepository>());
builder.Services.AddScoped<IEtlTaskLookup>(sp => sp.GetRequiredService<EtlTaskRepository>());
builder.Services.AddScoped<IAllEtlTasksProvider>(sp => sp.GetRequiredService<EtlTaskRepository>());
builder.Services.AddScoped<IRunHistorySink>(sp => sp.GetRequiredService<RunHistoryRepository>());

builder.Services.AddScoped<IDbConnectorFactory, DbConnectorFactory>();
builder.Services.AddScoped<EtlEngine>();
builder.Services.AddScoped<SchedulerService>();
builder.Services.AddScoped<EtlJob>();

// Banking control plane: kill switch + maintenance windows + failure webhook
builder.Services.AddSingleton<SchedulerKillSwitch>();
builder.Services.Configure<MaintenanceWindowsOptions>(builder.Configuration.GetSection("Maintenance"));
builder.Services.AddHttpClient();
// Streak-aware decorator wraps HTTP webhook：
//   - 預設 3 連敗才打 webhook（避免 alert fatigue）
//   - 從 alert 狀態恢復成功 → 也打一筆「[RECOVERY]」訊息
// 可透過 Webhooks:FailureStreakThreshold 調整。
builder.Services.AddSingleton<HttpFailureNotifier>();
builder.Services.AddSingleton<ICircuitBreakerEnforcer, DefaultCircuitBreakerEnforcer>();
builder.Services.AddSingleton<IFailureNotifier>(sp =>
{
    var inner = sp.GetRequiredService<HttpFailureNotifier>();
    var threshold = builder.Configuration.GetValue<int?>("Webhooks:FailureStreakThreshold") ?? 3;
    var recovery = builder.Configuration.GetValue<bool?>("Webhooks:NotifyRecovery") ?? true;
    if (threshold <= 1)
    {
        // threshold=1 等於每次失敗都通知；不需要 streak wrapper，直接用 inner
        return inner;
    }
    return new StreakAwareFailureNotifier(inner, threshold, recovery);
});

// Quartz
builder.Services.AddQuartz(q => { q.SchedulerName = "EtlTool"; });
builder.Services.AddQuartzHostedService(opt => { opt.WaitForJobsToComplete = true; });

// Migrations + scheduler bootstrap
builder.Services.AddHostedService<StartupBootstrapper>();

// Banking-grade reliability: nightly retention + periodic health + Prometheus metrics
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHostedService<RunHistoryRetentionService>();
builder.Services.AddHostedService<ConnectionHealthMonitor>();
builder.Services.AddHostedService<MetricsScraperService>();
builder.Services.AddHostedService<ApprovalExpirySweepService>();
builder.Services.AddHostedService<LongRunningJobWatchdog>();
builder.Services.AddHostedService<NightlyBackupService>();

// Authentication / Authorization
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<UserAuthService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Auth:Lockout").Get<LoginLockoutOptions>() ?? new LoginLockoutOptions());
builder.Services.AddSingleton<LoginLockoutService>();
// 兩個 service 都有 (IConfiguration) 與 (IEnumerable<string>) 兩個建構式，
// ValidateOnBuild 會抗議 ambiguous — 用 factory 明確指定走 IConfiguration 那條
builder.Services.AddSingleton<AdminIpAllowlistService>(sp =>
    new AdminIpAllowlistService(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ApiKeyAuthService>(sp =>
    new ApiKeyAuthService(sp.GetRequiredService<IConfiguration>()));

// Rate limiting on /api/* — 60 req/min per IP（保護後端 DB 不被監控腳本打爆）
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ip,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue<int?>("Api:RateLimitPerMinute") ?? 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
});
builder.Services.AddCascadingAuthenticationState();

// 讀 Auth 設定來決定 cookie ExpireTimeSpan（銀行預設 30 分鐘無操作即逾時）
var authOptsForCookie = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var sessionTimeoutMinutes = authOptsForCookie.ResolveTimeoutMinutes();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionTimeoutMinutes);
        options.Cookie.Name = "EtlTool.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("appdb");

// Blazor + Razor Pages (Razor Pages 用於 Login/Logout)
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// IIS 子應用程式 / 反向代理路徑前綴：例如部署在 https://server/etltool
// 設 PathBase=/etltool（環境變數 ETLTOOL_PATH_BASE 或 appsettings PathBase）
var pathBase = builder.Configuration["PathBase"]
               ?? Environment.GetEnvironmentVariable("ETLTOOL_PATH_BASE");
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);
}

// 反向代理 (IIS / nginx) 給的 X-Forwarded-* 標頭
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

// HTTPS 強制（Security:RequireHttps=true 才會 effective）— 必須在 Forwarded headers
// 之後（讓 X-Forwarded-Proto 生效），但在所有路由 / 認證之前（早 reject 比較省）。
// 可在啟動 log 看到「Security:RequireHttps=true, no HTTPS endpoint bound」警告。
app.UseRequireHttps();
{
    var requireHttps = builder.Configuration.GetValue<bool?>("Security:RequireHttps") ?? false;
    if (requireHttps)
    {
        var addresses = app.Urls;
        var hasHttps = addresses.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
        if (!hasHttps && addresses.Count > 0)
        {
            startupLogger.LogWarning(
                "⚠ Security:RequireHttps=true 但目前監聽的 endpoint 都不是 HTTPS：{Urls}。" +
                "若沒走反向代理（IIS/nginx 替你終端 TLS），所有請求都會被擋成 503。",
                string.Join(", ", addresses));
        }
        else
        {
            startupLogger.LogInformation("✓ Security:RequireHttps=true generated; HTTPS gate armed.");
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Banking 安全 headers（OWASP Secure Headers baseline）
app.UseSecurityHeaders();

app.UseAuthentication();
app.UseAuthorization();
// IP allowlist must run **after** authentication so we know if user is Admin,
// and **before** route execution so 403 is returned before page renders.
app.UseAdminIpAllowlist();
// API key check (only impacts /api/*; identity-aware UI paths skip this)
app.UseApiKeyAuth();
app.UseRateLimiter();
app.UseAntiforgery();
// 靜態資源（CSS / JS / 圖片 / Bootstrap / Inter 字體 fallback 等）必須匿名可達，
// 否則登入頁本身的樣式會破版且 favicon / blazor.web.js 會被導向回登入頁。
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous();

// Read-only JSON snapshot for external monitoring (Prometheus 已有 /metrics；
// 此端點提供更細的 per-task 物件結構)。
// Defence-in-depth: AdminIpAllowlist (前面 middleware) + Api:Keys (前面 middleware)
// + RateLimiter("api") 三層。
app.MapGet("/api/tasks/last-run", async (HttpContext ctx) =>
{
    var sp = ctx.RequestServices;
    await using var scope = sp.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlTool.Data.AppDbContext>();
    var runRepo = scope.ServiceProvider.GetRequiredService<EtlTool.Data.Repositories.RunHistoryRepository>();

    var tasks = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
        db.EtlTasks.AsNoTracking().OrderBy(t => t.Name));
    var lastSuccess = await runRepo.LastSuccessByTaskAsync(ctx.RequestAborted);
    var lastFailure = await runRepo.LastFailureByTaskAsync(ctx.RequestAborted);
    var sla = await runRepo.SuccessRateByTaskAsync(TimeSpan.FromDays(30), ctx.RequestAborted);

    var snapshot = tasks.Select(t => new
    {
        id = t.Id,
        name = t.Name,
        enabled = t.Enabled,
        autoDisabledAt = t.AutoDisabledAt,
        autoDisabledReason = t.AutoDisabledReason,
        cron = t.CronExpression,
        tags = t.Tags,
        lastSuccess = lastSuccess.TryGetValue(t.Id, out var ls) ? (DateTime?)ls : null,
        lastFailure = lastFailure.TryGetValue(t.Id, out var lf) ? (DateTime?)lf : null,
        sla30d = sla.TryGetValue(t.Id, out var s) && s.Total > 0
            ? new { successRate = Math.Round(s.Success * 100.0 / s.Total, 1), success = s.Success, total = s.Total }
            : null,
    }).ToList();

    ctx.Response.ContentType = "application/json; charset=utf-8";
    await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, new
    {
        generatedAt = DateTime.UtcNow,
        count = snapshot.Count,
        tasks = snapshot,
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}).AllowAnonymous().RequireRateLimiting("api");

// 單一 task 的詳細狀態（含最近 10 筆 runs）— monitoring 工具點擊 alert 時 drill-down 用
app.MapGet("/api/tasks/{taskId:guid}", async (Guid taskId, HttpContext ctx) =>
{
    var sp = ctx.RequestServices;
    await using var scope = sp.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlTool.Data.AppDbContext>();
    var runRepo = scope.ServiceProvider.GetRequiredService<EtlTool.Data.Repositories.RunHistoryRepository>();

    var t = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .FirstOrDefaultAsync(db.EtlTasks.AsNoTracking(), x => x.Id == taskId, ctx.RequestAborted);
    if (t is null)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("""{"error":"task not found"}""", ctx.RequestAborted);
        return;
    }

    var lastSuccess = await runRepo.LastSuccessByTaskAsync(ctx.RequestAborted);
    var lastFailure = await runRepo.LastFailureByTaskAsync(ctx.RequestAborted);
    var sla = await runRepo.SuccessRateByTaskAsync(TimeSpan.FromDays(30), ctx.RequestAborted);
    var recent = await runRepo.ListByTaskAsync(taskId, 10, ctx.RequestAborted);

    var detail = new
    {
        id = t.Id,
        name = t.Name,
        enabled = t.Enabled,
        autoDisabledAt = t.AutoDisabledAt,
        autoDisabledReason = t.AutoDisabledReason,
        cron = t.CronExpression,
        cronDescription = EtlTool.Core.Engine.CronHumanizer.Humanize(t.CronExpression),
        tags = t.Tags,
        notes = t.Notes,
        source = new { connectionId = t.SourceConnectionId, schema = t.SourceSchema, table = t.SourceTable },
        target = new { connectionId = t.TargetConnectionId, schema = t.TargetSchema, table = t.TargetTable },
        writeMode = t.WriteMode.ToString(),
        lastSuccess = lastSuccess.TryGetValue(t.Id, out var ls) ? (DateTime?)ls : null,
        lastFailure = lastFailure.TryGetValue(t.Id, out var lf) ? (DateTime?)lf : null,
        sla30d = sla.TryGetValue(t.Id, out var s) && s.Total > 0
            ? new { successRate = Math.Round(s.Success * 100.0 / s.Total, 1), success = s.Success, total = s.Total }
            : null,
        recentRuns = recent.Select(r => new
        {
            id = r.Id,
            startedAt = r.StartedAt,
            finishedAt = r.FinishedAt,
            durationSec = r.FinishedAt is null ? 0 : (r.FinishedAt.Value - r.StartedAt).TotalSeconds,
            status = r.Status.ToString(),
            triggerType = r.TriggerType.ToString(),
            rowsRead = r.RowsRead,
            rowsWritten = r.RowsWritten,
            errorMessage = r.ErrorMessage,
        }).ToList(),
    };

    ctx.Response.ContentType = "application/json; charset=utf-8";
    await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, detail,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}).AllowAnonymous().RequireRateLimiting("api");

// 單 task 的 RunHistory 分頁 — for downstream auditors / monitoring drill-down
//   GET /api/tasks/{id}/runs?page=1&size=20
//   page 從 1 起；size 上限 200 (避免 DOS)；超過範圍 → 空 items 但保留 total
app.MapGet("/api/tasks/{taskId:guid}/runs", async (Guid taskId, HttpContext ctx) =>
{
    int page = 1, size = 20;
    if (int.TryParse(ctx.Request.Query["page"], out var p) && p >= 1) page = p;
    if (int.TryParse(ctx.Request.Query["size"], out var sz) && sz >= 1) size = Math.Min(sz, 200);

    var sp = ctx.RequestServices;
    await using var scope = sp.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<EtlTool.Data.AppDbContext>();
    var runRepo = scope.ServiceProvider.GetRequiredService<EtlTool.Data.Repositories.RunHistoryRepository>();

    var t = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
        .FirstOrDefaultAsync(db.EtlTasks.AsNoTracking(), x => x.Id == taskId, ctx.RequestAborted);
    if (t is null)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("""{"error":"task not found"}""", ctx.RequestAborted);
        return;
    }

    var (items, total) = await runRepo.ListByTaskPagedAsync(taskId, page, size, ctx.RequestAborted);
    var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
    var payload = new
    {
        taskId,
        taskName = t.Name,
        page,
        size,
        total,
        totalPages,
        runs = items.Select(r => new
        {
            id = r.Id,
            startedAt = r.StartedAt,
            finishedAt = r.FinishedAt,
            durationSec = r.FinishedAt is null ? 0 : (r.FinishedAt.Value - r.StartedAt).TotalSeconds,
            status = r.Status.ToString(),
            triggerType = r.TriggerType.ToString(),
            rowsRead = r.RowsRead,
            rowsWritten = r.RowsWritten,
            errorMessage = r.ErrorMessage,
        }).ToList(),
    };
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, payload,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}).AllowAnonymous().RequireRateLimiting("api");

// /api/openapi.yaml — 手寫 OpenAPI 3.0 spec，可餵 Postman / Insomnia / openapi-generator
// 改 endpoint 時記得同步維護此 YAML。
app.MapGet("/api/openapi.yaml", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "application/yaml; charset=utf-8";
    await ctx.Response.WriteAsync(EtlTool.App.Services.OpenApiSpec.Yaml, ctx.RequestAborted);
}).AllowAnonymous().RequireRateLimiting("api");

// /api/health — JSON 版本的 health check（與 /healthz/detail 相同 payload，但 path 風格一致）
app.MapGet("/api/health", async (HttpContext ctx) =>
{
    var detail = await EtlTool.App.Services.DetailedHealthCheck.CollectAsync(ctx.RequestServices, ctx.RequestAborted);
    await EtlTool.App.Services.DetailedHealthCheck.WriteJsonAsync(ctx, detail);
}).AllowAnonymous().RequireRateLimiting("api");
// Detailed health JSON — db / quartz / connection monitor / audit write 各 component 細節
// 給銀行 ops 監控系統解析；簡單的 /healthz 仍保留供 LB / firewall 用
app.MapGet("/healthz/detail", async (HttpContext ctx) =>
{
    var detail = await DetailedHealthCheck.CollectAsync(ctx.RequestServices, ctx.RequestAborted);
    await DetailedHealthCheck.WriteJsonAsync(ctx, detail);
}).AllowAnonymous();
app.MapMetrics("/metrics").AllowAnonymous();   // Prometheus scrape endpoint (banks 應在 ingress / firewall 限制來源 IP)
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
