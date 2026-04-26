using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Services;

/// <summary>
/// EtlJob 執行完一次後呼叫；若連續失敗達到 threshold，自動把 task.Enabled 設為 false
/// 並從 scheduler 移除 trigger，避免持續打爆來源 DB / 灌爆 audit log。
///
/// threshold 解析：task.AutoDisableAfterFailures（per-task）覆寫
///                 Reliability:AutoDisableAfterFailures（全域）；皆 0/null = 停用機制。
/// </summary>
public sealed class DefaultCircuitBreakerEnforcer : ICircuitBreakerEnforcer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DefaultCircuitBreakerEnforcer> _log;

    public DefaultCircuitBreakerEnforcer(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<DefaultCircuitBreakerEnforcer> log)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _log = log;
    }

    public async Task OnRunCompleteAsync(EtlTask task, RunHistory run, CancellationToken ct)
    {
        // 只在最新一筆是 Failed 時才需要評估（成功的話 streak 已斷掉，不會觸發）
        if (run.Status != RunStatus.Failed) return;

        var globalDefault = _config.GetValue<int?>("Reliability:AutoDisableAfterFailures");
        var threshold = CircuitBreaker.ResolveThreshold(task.AutoDisableAfterFailures, globalDefault);
        if (threshold <= 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            var scheduler = scope.ServiceProvider.GetRequiredService<SchedulerService>();

            var recent = await db.RunHistories
                .AsNoTracking()
                .Where(r => r.EtlTaskId == task.Id)
                .OrderByDescending(r => r.StartedAt)
                .Take(threshold)
                .ToListAsync(ct);

            if (!CircuitBreaker.ShouldDisable(recent, threshold)) return;

            // Trip：把 task 設為 Disabled + audit + 從 scheduler 移除
            var t = await db.EtlTasks.FirstOrDefaultAsync(x => x.Id == task.Id, ct);
            if (t is null || !t.Enabled) return;  // 可能已被別人 disabled
            t.Enabled = false;
            await db.SaveChangesAsync(ct);

            await scheduler.UnscheduleAsync(task.Id, ct);

            await audit.LogAsync(
                AuditCategory.Task, AuditAction.Update,
                $"⛔ 任務「{task.Name}」連續失敗 {threshold} 次，已自動停用 (circuit-breaker)。Admin 請確認問題後手動重新啟用。",
                targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                severity: AuditSeverity.Warning, actor: "system", ct: ct);

            _log.LogWarning("Circuit-breaker tripped for task {TaskName} ({TaskId}): {Threshold} consecutive failures",
                task.Name, task.Id, threshold);
        }
        catch (Exception ex)
        {
            // 不能讓 circuit-breaker 自身的 bug 把 ETL 主流程拖下水
            _log.LogError(ex, "CircuitBreakerEnforcer failed for task {TaskId}; task remains enabled", task.Id);
        }
    }
}
