using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.Extensions.Logging;
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

    public EtlJob(EtlEngine engine, IEtlTaskLookup taskLookup, ILogger<EtlJob> log)
    {
        _engine = engine;
        _taskLookup = taskLookup;
        _log = log;
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

        await ExecuteWithRetryAsync(task, triggerType, context.CancellationToken);
    }

    /// <summary>
    /// 第 1 次依 triggerType 執行；後續重試一律以 TriggerType.Retry 標示。
    /// 每次嘗試 = 1 筆 RunHistory；exponential backoff 由 task 設定控制。
    /// </summary>
    internal Task ExecuteWithRetryAsync(EtlTask task, TriggerType triggerType, CancellationToken ct)
        => RetryPolicy.RunWithRetriesAsync(
            task, triggerType,
            attempt: async (trigger, cancel) =>
            {
                var run = await _engine.ExecuteAsync(task, trigger, cancel);
                return run.Status;
            },
            log: _log,
            ct: ct);
}

public interface IEtlTaskLookup
{
    Task<EtlTask?> GetWithMappingsAsync(Guid id, CancellationToken ct);
}
