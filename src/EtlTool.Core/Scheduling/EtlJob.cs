using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace EtlTool.Core.Scheduling;

[DisallowConcurrentExecution]
public sealed class EtlJob : IJob
{
    public const string TaskIdKey = "TaskId";
    public const string TriggerTypeKey = "TriggerType";

    private readonly EtlEngine _engine;
    private readonly IEtlTaskLookup _taskLookup;
    private readonly ILogger<EtlJob> _log;
    private readonly SchedulerKillSwitch? _killSwitch;
    private readonly IOptionsMonitor<MaintenanceWindowsOptions>? _maintenance;
    private readonly IMaintenanceWindowProvider? _maintenanceProvider;
    private readonly IAuditLogger? _audit;
    private readonly ICircuitBreakerEnforcer? _circuitBreaker;
    private readonly IRunHistorySink? _runSink;

    public EtlJob(
        EtlEngine engine, IEtlTaskLookup taskLookup, ILogger<EtlJob> log,
        SchedulerKillSwitch? killSwitch = null,
        IOptionsMonitor<MaintenanceWindowsOptions>? maintenance = null,
        IMaintenanceWindowProvider? maintenanceProvider = null,
        IAuditLogger? audit = null,
        ICircuitBreakerEnforcer? circuitBreaker = null,
        IRunHistorySink? runSink = null)
    {
        _engine = engine;
        _taskLookup = taskLookup;
        _log = log;
        _killSwitch = killSwitch;
        _maintenance = maintenance;
        _maintenanceProvider = maintenanceProvider;
        _audit = audit;
        _circuitBreaker = circuitBreaker;
        _runSink = runSink;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var taskIdStr = context.MergedJobDataMap.GetString(TaskIdKey)
            ?? throw new InvalidOperationException("JobDataMap missing TaskId.");
        var taskId = Guid.Parse(taskIdStr);

        var triggerType = TriggerType.Scheduled;
        if (context.MergedJobDataMap.TryGetString(TriggerTypeKey, out var trVal)
            && Enum.TryParse<TriggerType>(trVal, true, out var parsed))
        {
            triggerType = parsed;
        }

        var task = await _taskLookup.GetWithMappingsAsync(taskId, context.CancellationToken);
        if (task is null)
        {
            _log.LogWarning("EtlJob fired for missing task {TaskId}", taskId);
            return;
        }
        if (!task.Enabled && triggerType == TriggerType.Scheduled)
        {
            _log.LogDebug("Skipping disabled task {TaskId}", taskId);
            return;
        }

        // 銀行控制 1：全域 kill switch（手動觸發例外允許）
        if (_killSwitch is { IsPaused: true } && triggerType == TriggerType.Scheduled)
        {
            _log.LogInformation("Skipping {TaskName} — scheduler globally paused by {Actor} (reason: {Reason})",
                task.Name, _killSwitch.PausedBy, _killSwitch.PauseReason);
            if (_audit is not null)
                await _audit.LogAsync(AuditCategory.Run, AuditAction.RunStarted,
                    $"⏸ 跳過任務「{task.Name}」— 排程器全域暫停中（{_killSwitch.PauseReason ?? "未提供理由"}）",
                    targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                    severity: AuditSeverity.Warning, ct: context.CancellationToken);
            return;
        }

        // 銀行控制 2：維護視窗（手動觸發例外允許）
        // 優先用 IMaintenanceWindowProvider（可合併 DB + appsettings 來源）；
        // 沒注入則退化到舊的 IOptionsMonitor 直接查 appsettings — 保留向後相容。
        if (triggerType == TriggerType.Scheduled)
        {
            MaintenanceWindow? active = null;
            if (_maintenanceProvider is not null)
                active = await _maintenanceProvider.CurrentlyActiveAsync(DateTime.Now, context.CancellationToken);
            else if (_maintenance is not null)
                active = _maintenance.CurrentValue.CurrentlyActive(DateTime.Now);

            if (active is not null)
            {
                _log.LogInformation("Skipping {TaskName} — within maintenance window: {Reason}",
                    task.Name, active.Reason);
                if (_audit is not null)
                    await _audit.LogAsync(AuditCategory.Run, AuditAction.RunStarted,
                        $"⏸ 跳過任務「{task.Name}」— 維護視窗中（{active.Reason ?? $"{active.From}-{active.To}"}）",
                        targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                        severity: AuditSeverity.Info, ct: context.CancellationToken);
                return;
            }
        }

        // 銀行控制 3：上游依賴檢查（手動觸發例外允許）
        // 一個任務可宣告 DependsOnTaskIds = "guid1,guid2"，必須所有 parent 在過去 24h
        // 內有 Success 才會跑；否則跳過並寫 Audit Info。
        if (triggerType == TriggerType.Scheduled && _runSink is not null)
        {
            var deps = TaskDependencyChecker.ParseDependsOnIds(task.DependsOnTaskIds);
            if (deps.Count > 0)
            {
                try
                {
                    var lastSuccess = await _runSink.LastSuccessByTaskAsync(context.CancellationToken);
                    var lookback = (task.DependencyLookbackHours is { } h && h > 0) ? h : 24;
                    var depResult = TaskDependencyChecker.CheckDependencies(
                        deps, lastSuccess, DateTime.UtcNow, lookbackHours: lookback);
                    if (!depResult.AllSatisfied)
                    {
                        _log.LogInformation("Skipping {TaskName} — dependency unmet: {Reason}",
                            task.Name, depResult.Reason);
                        if (_audit is not null)
                            await _audit.LogAsync(AuditCategory.Run, AuditAction.RunStarted,
                                $"⏸ 跳過任務「{task.Name}」— {depResult.Reason}（unmet={depResult.UnsatisfiedParentIds.Count}）",
                                targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                                severity: AuditSeverity.Info, ct: context.CancellationToken);
                        return;
                    }

                    // Idempotency：若本任務已晚於所有 parent 的 LastSuccess 跑過 Success，
                    // 而 RunOncePerParentSuccess = true，表示此 cron tick 應該被跳過
                    // （已經在每個 parent 的 success batch 之後成功過了）。
                    if (task.RunOncePerParentSuccess
                        && lastSuccess.TryGetValue(task.Id, out var myLastSuccess))
                    {
                        DateTime? maxParentSuccess = null;
                        foreach (var pid in deps)
                        {
                            if (lastSuccess.TryGetValue(pid, out var p))
                                maxParentSuccess = maxParentSuccess is null || p > maxParentSuccess ? p : maxParentSuccess;
                        }
                        if (maxParentSuccess is not null && myLastSuccess > maxParentSuccess)
                        {
                            _log.LogInformation("Skipping {TaskName} — already succeeded after all parents (idempotent)", task.Name);
                            if (_audit is not null)
                                await _audit.LogAsync(AuditCategory.Run, AuditAction.RunStarted,
                                    $"⏸ 跳過任務「{task.Name}」— RunOncePerParentSuccess 已生效（最後成功 {myLastSuccess:HH:mm}Z 晚於所有 parent 最後成功 {maxParentSuccess:HH:mm}Z）",
                                    targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                                    severity: AuditSeverity.Info, ct: context.CancellationToken);
                            return;
                        }
                    }
                }
                catch (NotSupportedException) { /* sink doesn't support lookup → skip dependency check */ }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Dependency check threw for task {TaskName}; proceeding without check", task.Name);
                }
            }
        }

        await ExecuteWithRetryAsync(task, triggerType, context.CancellationToken);
    }

    /// <summary>
    /// 第 1 次依 triggerType 執行；後續重試一律以 TriggerType.Retry 標示。
    /// 每次嘗試 = 1 筆 RunHistory；exponential backoff 由 task 設定控制。
    /// 全部嘗試完成後（無論成功或失敗）通知 circuit-breaker，可能 auto-disable 此任務。
    /// </summary>
    internal async Task ExecuteWithRetryAsync(EtlTask task, TriggerType triggerType, CancellationToken ct)
    {
        RunHistory? lastRun = null;
        await RetryPolicy.RunWithRetriesAsync(
            task, triggerType,
            attempt: async (trigger, cancel) =>
            {
                var run = await _engine.ExecuteAsync(task, trigger, cancel);
                lastRun = run;
                // 把 ErrorMessage 帶回 retry policy，讓它能跑 classifier 短路
                return new RetryPolicy.AttemptResult(run.Status, run.ErrorMessage);
            },
            log: _log,
            ct: ct);

        // Circuit-breaker check — only meaningful if the final attempt failed.
        // 若機制未啟用 (_circuitBreaker = null 或 threshold = 0)，內部會 early return。
        if (_circuitBreaker is not null && lastRun is not null && lastRun.Status == RunStatus.Failed)
        {
            try
            {
                await _circuitBreaker.OnRunCompleteAsync(task, lastRun, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Circuit-breaker enforcer threw for task {TaskId}", task.Id);
            }
        }
    }
}

public interface IEtlTaskLookup
{
    Task<EtlTask?> GetWithMappingsAsync(Guid id, CancellationToken ct);
}
