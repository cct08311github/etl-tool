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
            var trigger = TriggerBuilder.Create()
                .WithIdentity($"trg_{task.Id}", "etl")
                .ForJob(jobKey)
                .WithCronSchedule(task.CronExpression)
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
