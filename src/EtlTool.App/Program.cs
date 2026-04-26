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
builder.Services.AddScoped<AuditQueryRepository>();
builder.Services.AddScoped<AuditChainVerifier>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

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

// Authentication / Authorization
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<UserAuthService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Auth:Lockout").Get<LoginLockoutOptions>() ?? new LoginLockoutOptions());
builder.Services.AddSingleton<LoginLockoutService>();
builder.Services.AddSingleton<AdminIpAllowlistService>();
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
app.UseAntiforgery();
// 靜態資源（CSS / JS / 圖片 / Bootstrap / Inter 字體 fallback 等）必須匿名可達，
// 否則登入頁本身的樣式會破版且 favicon / blazor.web.js 會被導向回登入頁。
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous();
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
