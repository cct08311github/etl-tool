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
builder.Services.AddScoped<AuditQueryRepository>();
builder.Services.AddSingleton<IAuditLogger, AuditLogger>();

builder.Services.AddScoped<IConnectionLookup>(sp => sp.GetRequiredService<ConnectionRepository>());
builder.Services.AddScoped<IEtlTaskLookup>(sp => sp.GetRequiredService<EtlTaskRepository>());
builder.Services.AddScoped<IAllEtlTasksProvider>(sp => sp.GetRequiredService<EtlTaskRepository>());
builder.Services.AddScoped<IRunHistorySink>(sp => sp.GetRequiredService<RunHistoryRepository>());

builder.Services.AddScoped<IDbConnectorFactory, DbConnectorFactory>();
builder.Services.AddScoped<EtlEngine>();
builder.Services.AddScoped<SchedulerService>();
builder.Services.AddScoped<EtlJob>();

// Quartz
builder.Services.AddQuartz(q => { q.SchedulerName = "EtlTool"; });
builder.Services.AddQuartzHostedService(opt => { opt.WaitForJobsToComplete = true; });

// Migrations + scheduler bootstrap
builder.Services.AddHostedService<StartupBootstrapper>();

// Authentication / Authorization
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<UserAuthService>();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
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
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
// 靜態資源（CSS / JS / 圖片 / Bootstrap / Inter 字體 fallback 等）必須匿名可達，
// 否則登入頁本身的樣式會破版且 favicon / blazor.web.js 會被導向回登入頁。
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
