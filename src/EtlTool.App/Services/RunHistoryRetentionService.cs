using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// 每日清理舊的 RunHistory。
/// 設計同 AuditRetentionService —— 啟動 60s 後跑一次、每天 03:30 跑（避開 audit 03:00）。
/// 政策從 RunHistory:RetentionDays + RunHistory:KeepLastPerTask 讀。
/// </summary>
public sealed class RunHistoryRetentionService : BackgroundService
{
    private const int BatchSize = 10000;
    private const int RunHourLocal = 3;
    private const int RunMinuteLocal = 30;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RunHistoryRetentionService> _log;

    public RunHistoryRetentionService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<RunHistoryRetentionService> log)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = NextLocalRun(DateTime.Now);
            var wait = nextRun - DateTime.Now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.FromMinutes(1);

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var policy = ReadPolicy();
        if (policy.KeepDays is null && policy.KeepLastPerTask is null)
        {
            _log.LogInformation("RunHistory retention disabled.");
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allRuns = await db.RunHistories
                .AsNoTracking()
                .Select(r => new RunHistory
                {
                    Id = r.Id,
                    EtlTaskId = r.EtlTaskId,
                    StartedAt = r.StartedAt,
                    Status = r.Status,        // 不需要但預設成 enum 不能 null
                    TriggerType = r.TriggerType,
                })
                .ToListAsync(ct);

            var toDelete = RunHistoryRetention.SelectIdsToDelete(allRuns, policy, DateTime.UtcNow);
            if (toDelete.Count == 0)
            {
                _log.LogInformation("RunHistory retention: nothing to delete (total={Total}).", allRuns.Count);
                return;
            }

            int totalDeleted = 0;
            foreach (var batch in toDelete.Chunk(BatchSize))
            {
                var set = batch.ToHashSet();
                var deleted = await db.RunHistories
                    .Where(r => set.Contains(r.Id))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += deleted;
            }

            _log.LogInformation("RunHistory retention: deleted {Deleted}/{Total}.", totalDeleted, allRuns.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RunHistory retention run failed.");
        }
    }

    private RunHistoryRetentionPolicy ReadPolicy()
    {
        var section = _config.GetSection("RunHistory");
        int? days = section.GetValue<int?>("RetentionDays");
        int? perTask = section.GetValue<int?>("KeepLastPerTask");
        if (days <= 0) days = null;
        if (perTask <= 0) perTask = null;
        return new RunHistoryRetentionPolicy(days, perTask);
    }

    public static DateTime NextLocalRun(DateTime now)
    {
        var todayRun = new DateTime(now.Year, now.Month, now.Day, RunHourLocal, RunMinuteLocal, 0, DateTimeKind.Local);
        return now < todayRun ? todayRun : todayRun.AddDays(1);
    }
}
