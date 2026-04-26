using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// 把現有狀態定期同步到 Prometheus gauges。
/// 為了避免改動 Core 層拉進 prometheus-net 依賴，採「拉模式」：
///   - 每 30 秒刷一次 connection 健康 gauge
///   - 每 30 秒刷一次 scheduler paused gauge
///   - 每 30 秒拉最近一次以後新增的 RunHistory + AuditEvents 增量更新 counter
///     （用記憶體 cursor 記錄 last 處理過的 At；重啟後從 0 開始累積，這在
///      single-instance + Prometheus rate() 下沒問題 — counter 不允許下降但
///      重設到 0 後 Prometheus 自動處理 reset 偵測）
/// </summary>
public sealed class MetricsScraperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SchedulerKillSwitch _killSwitch;
    private readonly ILogger<MetricsScraperService> _log;

    private DateTime _lastRunCursor = DateTime.MinValue;
    private DateTime _lastAuditCursor = DateTime.MinValue;

    public MetricsScraperService(IServiceScopeFactory scopeFactory, SchedulerKillSwitch killSwitch, ILogger<MetricsScraperService> log)
    {
        _scopeFactory = scopeFactory;
        _killSwitch = killSwitch;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScrapeOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "MetricsScraper cycle failed; will retry next interval.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ScrapeOnceAsync(CancellationToken ct)
    {
        // 1) Scheduler paused gauge
        EtlMetrics.SchedulerPaused.Set(_killSwitch.IsPaused ? 1 : 0);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 2) Connection health gauges
        var conns = await db.Connections
            .AsNoTracking()
            .Select(c => new { c.Name, c.LastCheckOk })
            .ToListAsync(ct);
        foreach (var c in conns)
        {
            EtlMetrics.ConnectionHealth
                .WithLabels(c.Name)
                .Set(c.LastCheckOk == true ? 1 : 0);
        }

        // 3) Run history increment
        var newRuns = await db.RunHistories
            .AsNoTracking()
            .Where(r => r.StartedAt > _lastRunCursor && r.FinishedAt != null)
            .ToListAsync(ct);
        if (newRuns.Count > 0)
        {
            // 撈 task name；id-name map
            var taskIds = newRuns.Select(r => r.EtlTaskId).Distinct().ToList();
            var nameMap = await db.EtlTasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

            foreach (var r in newRuns)
            {
                var name = nameMap.GetValueOrDefault(r.EtlTaskId, r.EtlTaskId.ToString());
                EtlMetrics.Runs.WithLabels(name, r.Status.ToString()).Inc();
                EtlMetrics.RowsRead.WithLabels(name).Inc(r.RowsRead);
                EtlMetrics.RowsWritten.WithLabels(name).Inc(r.RowsWritten);
                if (r.FinishedAt is { } finished)
                {
                    EtlMetrics.RunDuration.WithLabels(name)
                        .Observe((finished - r.StartedAt).TotalSeconds);
                }
            }
            _lastRunCursor = newRuns.Max(r => r.StartedAt);
        }

        // 4) Audit increment
        var newAudit = await db.AuditEvents
            .AsNoTracking()
            .Where(a => a.At > _lastAuditCursor)
            .Select(a => new { a.At, a.Category, a.Severity })
            .ToListAsync(ct);
        if (newAudit.Count > 0)
        {
            foreach (var a in newAudit)
            {
                EtlMetrics.AuditEvents
                    .WithLabels(a.Category.ToString(), a.Severity.ToString())
                    .Inc();
            }
            _lastAuditCursor = newAudit.Max(a => a.At);
        }
    }
}
