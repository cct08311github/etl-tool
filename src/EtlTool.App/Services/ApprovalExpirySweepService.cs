using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// 兩人覆核請求每日掃描：把 Pending 但 ExpiresAt &lt; UtcNow 的請求標記為 Expired，
/// 並寫一筆 audit。
///
/// 設計：
///   - 每天本地時間 03:15（緊接 audit retention 03:00 之後）
///   - 啟動時也跑一次（避免長時間離線後復電才掃）
///   - 失敗只 log，下次再試
///   - **不會自動刪 Expired 的紀錄** — 那是稽核軌跡，由 audit retention 一起留
/// </summary>
public sealed class ApprovalExpirySweepService : BackgroundService
{
    private const int RunHourLocal = 3;
    private const int RunMinuteLocal = 15;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ApprovalExpirySweepService> _log;

    public ApprovalExpirySweepService(IServiceScopeFactory scopeFactory, ILogger<ApprovalExpirySweepService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        await RunOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRun = NextLocalRun(DateTime.Now, RunHourLocal, RunMinuteLocal);
            var wait = nextRun - DateTime.Now;
            if (wait < TimeSpan.Zero) wait = TimeSpan.FromMinutes(1);

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }

            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

            var now = DateTime.UtcNow;
            var expired = await db.ApprovalRequests
                .Where(r => r.Status == ApprovalStatus.Pending && r.ExpiresAt <= now)
                .ToListAsync(ct);

            if (expired.Count == 0)
            {
                _log.LogDebug("ApprovalExpirySweep: no expired pending requests.");
                return;
            }

            foreach (var r in expired)
            {
                r.Status = ApprovalStatus.Expired;
                r.DecidedAt = now;
                // DecidedBy 留 null — 系統自動，不是人為決定
            }
            await db.SaveChangesAsync(ct);

            foreach (var r in expired)
            {
                await audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
                    $"⏱ 覆核請求逾期自動失效：「{r.TargetName}」(提交者 {r.SubmittedBy}, 已等候 {(now - r.SubmittedAt).TotalDays:F0} 天)",
                    targetType: r.TargetType, targetId: r.TargetId, targetName: r.TargetName,
                    severity: AuditSeverity.Info, actor: "system", ct: ct);
            }

            _log.LogInformation("ApprovalExpirySweep: marked {Count} pending requests as Expired.", expired.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ApprovalExpirySweep run failed; will retry next cycle.");
        }
    }

    /// <summary>下一個 (HH:MM) 本地時間；若今天那個時刻已過 → 明天的相同時刻。</summary>
    public static DateTime NextLocalRun(DateTime now, int hourLocal, int minuteLocal)
    {
        var todayRun = new DateTime(now.Year, now.Month, now.Day, hourLocal, minuteLocal, 0, DateTimeKind.Local);
        return now < todayRun ? todayRun : todayRun.AddDays(1);
    }
}
