using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

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

        // Quartz host service 會稍後啟動，這裡先註冊好 jobs
        var scheduler = scope.ServiceProvider.GetRequiredService<SchedulerService>();
        await scheduler.InitializeAsync(ct);

        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
        await audit.LogAsync(AuditCategory.System, AuditAction.SystemStart,
            "系統啟動完成", ct: ct);
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
