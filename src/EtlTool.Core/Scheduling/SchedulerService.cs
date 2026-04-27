using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.Extensions.Logging;
using Quartz;

namespace EtlTool.Core.Scheduling;

/// <summary>
/// 維護 Quartz scheduler 內每個 EtlTask 對應的 JobDetail + Trigger。
/// 啟動時呼叫 InitializeAsync，任務新增/修改/刪除時呼叫 RescheduleAsync / UnscheduleAsync。
/// </summary>
public sealed class SchedulerService
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IEtlTaskLookup _lookup;
    private readonly IAllEtlTasksProvider _allTasksProvider;
    private readonly ILogger<SchedulerService> _log;
    private readonly IAuditLogger? _audit;

    public SchedulerService(
        ISchedulerFactory schedulerFactory,
        IEtlTaskLookup lookup,
        IAllEtlTasksProvider allTasksProvider,
        ILogger<SchedulerService> log,
        IAuditLogger? audit = null)
    {
        _schedulerFactory = schedulerFactory;
        _lookup = lookup;
        _allTasksProvider = allTasksProvider;
        _log = log;
        _audit = audit;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var tasks = await _allTasksProvider.GetAllAsync(ct);
        foreach (var task in tasks.Where(t => t.Enabled))
        {
            await ScheduleInternalAsync(scheduler, task, ct);
        }
        var activeCount = tasks.Count(t => t.Enabled);
        _log.LogInformation("Scheduler initialized with {Count} active tasks", activeCount);
        if (_audit is not null)
            await _audit.LogAsync(AuditCategory.Scheduler, AuditAction.SchedulerInitialized,
                $"排程器已啟動，已註冊 {activeCount} 個任務",
                ct: ct);
    }

    public async Task RescheduleAsync(Guid taskId, CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var jobKey = JobKeyFor(taskId);
        await scheduler.DeleteJob(jobKey, ct);

        var task = await _lookup.GetWithMappingsAsync(taskId, ct);
        if (task is not null && task.Enabled)
        {
            await ScheduleInternalAsync(scheduler, task, ct);
        }
    }

    public async Task UnscheduleAsync(Guid taskId, CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        await scheduler.DeleteJob(JobKeyFor(taskId), ct);
    }

    /// <summary>
    /// 暫停一個 trigger（通常 group=etl, name="trg_{taskId}"）。
    /// 與 Enabled=false 不同：Enabled=false 會把 trigger 整個移除；
    /// PauseTrigger 只暫停下次觸發，trigger metadata + cron expression 保留，
    /// admin 可以隨時 ResumeTrigger 而不必重新設定。
    /// 適用情境：來源 DB 維護中，1 小時後恢復。
    /// </summary>
    public async Task PauseTriggerAsync(string triggerName, string triggerGroup, string? actor, CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var key = new TriggerKey(triggerName, triggerGroup);
        await scheduler.PauseTrigger(key, ct);
        if (_audit is not null)
            await _audit.LogAsync(AuditCategory.Scheduler, AuditAction.Update,
                $"⏸ 暫停 Quartz trigger {triggerGroup}.{triggerName}（task 設定不變）",
                severity: AuditSeverity.Warning, actor: actor ?? "system", ct: ct);
    }

    public async Task ResumeTriggerAsync(string triggerName, string triggerGroup, string? actor, CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var key = new TriggerKey(triggerName, triggerGroup);
        await scheduler.ResumeTrigger(key, ct);
        if (_audit is not null)
            await _audit.LogAsync(AuditCategory.Scheduler, AuditAction.Update,
                $"▶ 恢復 Quartz trigger {triggerGroup}.{triggerName}",
                severity: AuditSeverity.Info, actor: actor ?? "system", ct: ct);
    }

    public async Task TriggerNowAsync(Guid taskId, CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var jobKey = JobKeyFor(taskId);

        var task = await _lookup.GetWithMappingsAsync(taskId, ct)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");

        if (!await scheduler.CheckExists(jobKey, ct))
        {
            await ScheduleInternalAsync(scheduler, task, ct, includeTrigger: false);
        }

        var data = new JobDataMap
        {
            [EtlJob.TaskIdKey] = taskId.ToString(),
            [EtlJob.TriggerTypeKey] = TriggerType.Manual.ToString(),
        };
        await scheduler.TriggerJob(jobKey, data, ct);

        if (_audit is not null)
            await _audit.LogAsync(AuditCategory.Scheduler, AuditAction.TriggerNow,
                $"手動觸發任務「{task.Name}」",
                targetType: nameof(EtlTask), targetId: taskId, targetName: task.Name, ct: ct);
    }

    public async Task<DateTimeOffset?> GetNextFireTimeAsync(Guid taskId, CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var triggers = await scheduler.GetTriggersOfJob(JobKeyFor(taskId), ct);
        return triggers.Select(t => t.GetNextFireTimeUtc()).Where(d => d.HasValue).Min();
    }

    /// <summary>
    /// 提供給 admin /scheduler 頁面：列出所有註冊的 jobs + 對應 triggers (next/prev fire time +
    /// state) + 目前正在執行的 job 詳情。所有資料以 read-only snapshot 回傳。
    /// </summary>
    public async Task<SchedulerInspection> InspectAsync(CancellationToken ct)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);

        var inStandby = scheduler.InStandbyMode;
        var isShutdown = scheduler.IsShutdown;
        var schedulerName = scheduler.SchedulerName;

        var allJobKeys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup(), ct);

        var jobs = new List<JobInspection>();
        foreach (var key in allJobKeys.OrderBy(k => k.Name))
        {
            var detail = await scheduler.GetJobDetail(key, ct);
            var taskIdStr = detail?.JobDataMap.GetString(EtlJob.TaskIdKey);
            Guid? taskId = Guid.TryParse(taskIdStr, out var tid) ? tid : null;

            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            var triggerInfos = new List<TriggerInspection>();
            foreach (var t in triggers)
            {
                var state = await scheduler.GetTriggerState(t.Key, ct);
                triggerInfos.Add(new TriggerInspection(
                    Name: t.Key.Name,
                    Group: t.Key.Group,
                    State: state.ToString(),
                    Description: t is ICronTrigger ct2 ? ct2.CronExpressionString : t.GetType().Name,
                    PreviousFireTimeUtc: t.GetPreviousFireTimeUtc(),
                    NextFireTimeUtc: t.GetNextFireTimeUtc()));
            }

            jobs.Add(new JobInspection(
                Group: key.Group,
                Name: key.Name,
                TaskId: taskId,
                Triggers: triggerInfos));
        }

        var executing = await scheduler.GetCurrentlyExecutingJobs(ct);
        var current = executing.Select(c => new CurrentlyExecuting(
            JobName: c.JobDetail.Key.Name,
            JobGroup: c.JobDetail.Key.Group,
            FireTimeUtc: c.FireTimeUtc.UtcDateTime,
            RunMs: (long)c.JobRunTime.TotalMilliseconds)).ToList();

        return new SchedulerInspection(
            SchedulerName: schedulerName,
            InStandby: inStandby,
            IsShutdown: isShutdown,
            Jobs: jobs,
            CurrentlyExecuting: current);
    }

    private static async Task ScheduleInternalAsync(
        IScheduler scheduler, EtlTask task, CancellationToken ct, bool includeTrigger = true)
    {
        var jobKey = JobKeyFor(task.Id);
        var job = JobBuilder.Create<EtlJob>()
            .WithIdentity(jobKey)
            .UsingJobData(EtlJob.TaskIdKey, task.Id.ToString())
            .StoreDurably()
            .Build();

        await scheduler.AddJob(job, replace: true, storeNonDurableWhileAwaitingScheduling: false, ct);

        if (includeTrigger && !string.IsNullOrWhiteSpace(task.CronExpression))
        {
            // 銀行可靠度：misfire policy = DoNothing
            // 若 service 重啟或 thread pool 飽和導致 trigger 錯過，**不要補跑**
            // 直接等下一次正常排程即可。否則開機後可能瞬間觸發數十個堆積的 job，
            // 把目標 DB 打爆，且資料時間語意（${YESTERDAY} 等 token）會錯亂。
            var trigger = TriggerBuilder.Create()
                .WithIdentity($"trg_{task.Id}", "etl")
                .ForJob(jobKey)
                .WithCronSchedule(task.CronExpression, csb => csb
                    .WithMisfireHandlingInstructionDoNothing())
                .Build();
            await scheduler.ScheduleJob(trigger, ct);
        }
    }

    private static JobKey JobKeyFor(Guid taskId) => new($"etl_{taskId}", "etl");
}

public interface IAllEtlTasksProvider
{
    Task<IReadOnlyList<EtlTask>> GetAllAsync(CancellationToken ct);
}

/// <summary>給 admin 排程檢視頁面。</summary>
public sealed record SchedulerInspection(
    string SchedulerName,
    bool InStandby,
    bool IsShutdown,
    IReadOnlyList<JobInspection> Jobs,
    IReadOnlyList<CurrentlyExecuting> CurrentlyExecuting);

public sealed record JobInspection(
    string Group,
    string Name,
    Guid? TaskId,
    IReadOnlyList<TriggerInspection> Triggers);

public sealed record TriggerInspection(
    string Name,
    string Group,
    string State,
    string? Description,
    DateTimeOffset? PreviousFireTimeUtc,
    DateTimeOffset? NextFireTimeUtc);

public sealed record CurrentlyExecuting(
    string JobName,
    string JobGroup,
    DateTimeOffset FireTimeUtc,
    long RunMs);
