using EtlTool.App.Auth;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EtlTool.App.Services;

/// <summary>啟動時：套用 EF Core migrations、初始化 Quartz scheduler。</summary>
public sealed class StartupBootstrapper : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupBootstrapper> _log;

    public StartupBootstrapper(IServiceScopeFactory scopeFactory, ILogger<StartupBootstrapper> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
        _log.LogInformation("Database migrated to latest schema.");

        // 補填 audit 鏈結：之前 hash chain 還沒上線就存在的 audit events
        await BackfillAuditHashesAsync(db, ct);

        // RBAC bootstrap：Users 表空時，從 appsettings Auth 段建第一個 admin
        await SeedDefaultAdminAsync(scope, db, ct);

        // Data Protection keys 目錄權限檢查（POSIX）— 防止連線字串解密金鑰被同機其他 user 讀取
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var hostEnv = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var dataDir = config["DataDirectory"]
                      ?? Environment.GetEnvironmentVariable("ETLTOOL_DATA_DIR")
                      ?? Path.Combine(hostEnv.ContentRootPath, "data");
        var keysDir = Path.Combine(dataDir, "keys");
        await DataDirPermissionCheck.RunAndAuditAsync(keysDir, audit, _log, ct);

        // Quartz host service 會稍後啟動，這裡先註冊好 jobs
        var scheduler = scope.ServiceProvider.GetRequiredService<SchedulerService>();
        await scheduler.InitializeAsync(ct);

        await audit.LogAsync(AuditCategory.System, AuditAction.SystemStart,
            "系統啟動完成", ct: ct);
    }

    private async Task BackfillAuditHashesAsync(AppDbContext db, CancellationToken ct)
    {
        // 只做一次性 backfill：找出 Hash 為 null 的，按時序補
        // 注意：之後若重新 backfill 整鏈，原有 hash 會被覆蓋（這對「歷史竄改偵測」有風險）
        // 因此只補 Hash IS NULL 的，已 hash 的不動
        var needsBackfill = await db.AuditEvents
            .Where(e => e.Hash == null)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToListAsync(ct);

        if (needsBackfill.Count == 0) return;

        _log.LogInformation("Backfilling hash chain for {Count} pre-existing audit events…", needsBackfill.Count);

        // 取「最後一筆有 hash 的」作為起始 prev
        var prev = await db.AuditEvents
            .Where(e => e.Hash != null)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync(ct);

        foreach (var e in needsBackfill)
        {
            e.PreviousHash = prev;
            e.Hash = AuditHasher.ComputeHash(e, prev);
            prev = e.Hash;
        }

        await db.SaveChangesAsync(ct);
        _log.LogInformation("Audit hash chain backfilled.");
    }

    private async Task SeedDefaultAdminAsync(IServiceScope scope, AppDbContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            _log.LogDebug("Users table non-empty; skip default admin seed.");
            return;
        }

        var opts = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var username = string.IsNullOrWhiteSpace(opts.Username) ? "admin" : opts.Username;
        string passwordHash;

        if (!string.IsNullOrEmpty(opts.PasswordHash))
        {
            passwordHash = opts.PasswordHash;
        }
        else if (!string.IsNullOrEmpty(opts.Password))
        {
            passwordHash = UserAuthService.Hash(opts.Password);
        }
        else
        {
            passwordHash = UserAuthService.Hash(UserAuthService.DefaultDevPassword);
            _log.LogWarning("[SECURITY] Seeded default admin '{Username}' with built-in default password '{Default}'. 請立即修改。",
                username, UserAuthService.DefaultDevPassword);
        }

        var admin = new User
        {
            Username = username,
            PasswordHash = passwordHash,
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        await audit.LogAsync(AuditCategory.Auth, AuditAction.Create,
            $"從 appsettings 啟動 seed 出第一個 Admin「{username}」",
            targetType: nameof(User), targetId: admin.Id, targetName: username,
            actor: "system", severity: AuditSeverity.Info, ct: ct);

        _log.LogInformation("Seeded initial admin user: {Username}", username);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            await audit.LogAsync(AuditCategory.System, AuditAction.SystemStop, "系統正在停止", ct: ct);
        }
        catch { /* shutdown 期間最佳努力 */ }
    }
}
