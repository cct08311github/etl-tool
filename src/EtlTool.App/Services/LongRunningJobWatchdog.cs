using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using EtlTool.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace EtlTool.App.Services;

/// <summary>
/// 每 60 秒掃一次 Quartz currently-executing jobs；對執行時間超過 task.MaxRunMinutes
/// （或全域預設 LongRunningJob:MaxMinutes）的 job 發 Warning audit。
///
/// 同一個 (taskId, runId) 只通知一次，避免每分鐘洗版。狀態僅 in-memory，重啟後重置。
/// </summary>
public sealed class LongRunningJobWatchdog : BackgroundService
{
    private const int DefaultMaxMinutes = 30;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<LongRunningJobWatchdog> _log;
    // 哪些 job 已經被通知過（以 jobName + fireInstanceId 組合 key），避免每輪重發。
    private readonly HashSet<string> _alreadyAlerted = new();

    public LongRunningJobWatchdog(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<LongRunningJobWatchdog> log)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動延遲 90 秒（避免 Quartz host 還沒 initialize）
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "LongRunningJobWatchdog scan failed; will retry."); }

            try { await Task.Delay(ScanInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var schedFactory = scope.ServiceProvider.GetService<ISchedulerFactory>();
        if (schedFactory is null) return;
        var scheduler = await schedFactory.GetScheduler(ct);
        if (scheduler.IsShutdown) return;

        var executing = await scheduler.GetCurrentlyExecutingJobs(ct);
        if (executing.Count == 0) return;

        var globalMax = _config.GetValue<int?>("LongRunningJob:MaxMinutes") ?? DefaultMaxMinutes;
        if (globalMax <= 0) globalMax = DefaultMaxMinutes;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();

        foreach (var ctx in executing)
        {
            ct.ThrowIfCancellationRequested();
            var key = $"{ctx.JobDetail.Key}|{ctx.FireInstanceId}";
            if (_alreadyAlerted.Contains(key)) continue;

            var taskIdStr = ctx.JobDetail.JobDataMap.GetString(EtlJob.TaskIdKey);
            if (!Guid.TryParse(taskIdStr, out var taskId)) continue;

            var runMinutes = ctx.JobRunTime.TotalMinutes;

            // 取此 task 的 override；沒設用 globalMax
            var task = await db.EtlTasks.AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new { t.Name, t.MaxRunMinutes })
                .FirstOrDefaultAsync(ct);
            if (task is null) continue;

            var threshold = (task.MaxRunMinutes is { } m && m > 0) ? m : globalMax;
            if (runMinutes < threshold) continue;

            // 觸發 audit
            try
            {
                await audit.LogAsync(
                    AuditCategory.Run, AuditAction.RunStarted,
                    $"⏱ 任務「{task.Name}」執行已達 {runMinutes:F0} 分鐘（閾值 {threshold} 分鐘）— 仍在執行中，watchdog 提醒",
                    targetType: nameof(EtlTask), targetId: taskId, targetName: task.Name,
                    severity: AuditSeverity.Warning, actor: "system", ct: ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to write long-running audit for task {TaskId}", taskId);
            }
            _alreadyAlerted.Add(key);
            _log.LogWarning("Long-running ETL detected: {TaskName} ({Minutes:F1}m, threshold={Threshold}m)",
                task.Name, runMinutes, threshold);
        }

        // 偶爾整理 set 大小：currently executing 都不在 set 裡 → 清掉舊 keys
        // 簡化：set 大於 1000 就清空。在實務上不會這麼多 in-flight job。
        if (_alreadyAlerted.Count > 1000) _alreadyAlerted.Clear();
    }
}
