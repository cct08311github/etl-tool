using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// 每日跑一次 audit log 清理。讀 appsettings 的 Audit:RetentionDays / Audit:KeepLastPerCategory，
/// 用 Core 的 AuditRetention.SelectIdsToDelete 計算該刪 ID，分批 delete。
///
/// 設計：
///   - 每天本地時間 03:00 跑（避開大多數 ETL 排程時段）
///   - App 一啟動先跑一次（避免重啟才有清理效果）
///   - 用獨立 DI scope 避免污染主流程
///   - 失敗只 log 不再丟（hosted service 拋例外會 crash app）
///   - 每次最多刪 10000 筆（避免 SQLite 鎖太久）
/// </summary>
public sealed class AuditRetentionService : BackgroundService
{
    private const int BatchSize = 10000;
    private const int RunHourLocal = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AuditRetentionService> _log;

    public AuditRetentionService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<AuditRetentionService> log)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動時延遲 30 秒（讓 EF migration 跑完）
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // 啟動時跑一次
        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = NextLocalRun(DateTime.Now, RunHourLocal);
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
        if (policy.KeepDays is null && policy.KeepLastPerCategory is null)
        {
            _log.LogInformation("Audit retention disabled (Audit:RetentionDays and Audit:KeepLastPerCategory both unset).");
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allEvents = await db.AuditEvents
                .AsNoTracking()
                .Select(e => new AuditEvent
                {
                    Id = e.Id,
                    At = e.At,
                    Category = e.Category,
                    // 其餘欄位 retention 邏輯不需要 → 不撈節省記憶體
                    Action = e.Action,
                    Severity = e.Severity,
                    Message = "",
                })
                .ToListAsync(ct);

            var idsToDelete = AuditRetention.SelectIdsToDelete(allEvents, policy, DateTime.UtcNow);
            if (idsToDelete.Count == 0)
            {
                _log.LogInformation("Audit retention: no events to delete (policy={Policy}, total={Total}).", policy, allEvents.Count);
                return;
            }

            int totalDeleted = 0;
            foreach (var batch in idsToDelete.Chunk(BatchSize))
            {
                var batchSet = batch.ToHashSet();
                var deleted = await db.AuditEvents
                    .Where(e => batchSet.Contains(e.Id))
                    .ExecuteDeleteAsync(ct);
                totalDeleted += deleted;
            }

            _log.LogInformation("Audit retention: deleted {Deleted}/{Total} events (policy={Policy}).",
                totalDeleted, allEvents.Count, policy);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Audit retention run failed; will retry next cycle.");
        }
    }

    private AuditRetentionPolicy ReadPolicy()
    {
        var section = _config.GetSection("Audit");
        int? days = section.GetValue<int?>("RetentionDays");
        int? perCat = section.GetValue<int?>("KeepLastPerCategory");
        // 0 / 負數視為「未設定」
        if (days <= 0) days = null;
        if (perCat <= 0) perCat = null;
        return new AuditRetentionPolicy(days, perCat);
    }

    /// <summary>下一個 03:00 (本地時間)。若已過今天 03:00 → 明天 03:00。</summary>
    public static DateTime NextLocalRun(DateTime now, int hourLocal)
    {
        var todayRun = new DateTime(now.Year, now.Month, now.Day, hourLocal, 0, 0, DateTimeKind.Local);
        return now < todayRun ? todayRun : todayRun.AddDays(1);
    }
}
