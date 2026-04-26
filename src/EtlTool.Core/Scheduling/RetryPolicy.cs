using EtlTool.Core.Models;
using Microsoft.Extensions.Logging;

namespace EtlTool.Core.Scheduling;

/// <summary>
/// 對單次 Quartz fire 內的「執行 + 失敗重試」邏輯進行抽象。
/// - 第 1 次嘗試使用呼叫者帶進來的 trigger（Scheduled / Manual）
/// - 第 2 次起以 TriggerType.Retry 標示
/// - 每次嘗試呼叫者各自 produce 一筆 RunHistory（參數 attempt 是 IRunHistorySink 內由 ExecuteAsync 自行寫入）
/// 抽出來是為了單元測試 — 不用 mock EtlEngine（sealed）。
/// </summary>
public static class RetryPolicy
{
    public sealed record Result(int TotalAttempts, bool FinalSucceeded);

    public static async Task<Result> RunWithRetriesAsync(
        EtlTask task,
        TriggerType initialTrigger,
        Func<TriggerType, CancellationToken, Task<RunStatus>> attempt,
        ILogger log,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? sleeper = null)
    {
        sleeper ??= (delay, c) => Task.Delay(delay, c);

        int attemptIndex = 0; // 0 = 第一次
        double currentDelay = task.RetryDelaySeconds;

        while (true)
        {
            var thisTrigger = attemptIndex == 0 ? initialTrigger : TriggerType.Retry;
            var status = await attempt(thisTrigger, ct);

            if (status == RunStatus.Success)
                return new Result(attemptIndex + 1, true);

            if (attemptIndex >= task.MaxRetries)
            {
                if (task.MaxRetries > 0)
                    log.LogWarning("Task {TaskName} exhausted {Max} retries; giving up.",
                        task.Name, task.MaxRetries);
                return new Result(attemptIndex + 1, false);
            }

            attemptIndex++;
            log.LogInformation("Task {TaskName} failed; retry {Attempt}/{Max} in {Delay}s",
                task.Name, attemptIndex, task.MaxRetries, currentDelay);

            try { await sleeper(TimeSpan.FromSeconds(currentDelay), ct); }
            catch (OperationCanceledException) { return new Result(attemptIndex, false); }

            currentDelay = Math.Max(1, currentDelay * task.RetryBackoffMultiplier);
        }
    }
}
