using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.Extensions.Logging;

namespace EtlTool.Core.Scheduling;

/// <summary>
/// 對單次 Quartz fire 內的「執行 + 失敗重試」邏輯進行抽象。
/// - 第 1 次嘗試使用呼叫者帶進來的 trigger（Scheduled / Manual）
/// - 第 2 次起以 TriggerType.Retry 標示
/// - 每次嘗試呼叫者各自 produce 一筆 RunHistory（參數 attempt 是 IRunHistorySink 內由 ExecuteAsync 自行寫入）
/// 抽出來是為了單元測試 — 不用 mock EtlEngine（sealed）。
///
/// Classifier-aware 短路：attempt 失敗後，用 EngineErrorClassifier 判斷錯誤類別。
/// 若分類為 Permanent（schema 不存在 / auth 失敗 / SQL syntax / PK 違反 …），
/// 立刻放棄重試 — 重試這類錯誤只是浪費 retry 額度與 DB 壓力。
/// </summary>
public static class RetryPolicy
{
    public sealed record Result(int TotalAttempts, bool FinalSucceeded);

    /// <summary>Skipped-due-to-permanent-error 結果，給呼叫方在 log 中區分。</summary>
    public sealed record AttemptResult(RunStatus Status, string? ErrorMessage);

    public static async Task<Result> RunWithRetriesAsync(
        EtlTask task,
        TriggerType initialTrigger,
        Func<TriggerType, CancellationToken, Task<AttemptResult>> attempt,
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
            var result = await attempt(thisTrigger, ct);

            if (result.Status == RunStatus.Success)
                return new Result(attemptIndex + 1, true);

            // Classifier short-circuit：永久性錯誤直接放棄，不消耗 retry 額度
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                var classification = EngineErrorClassifier.Classify(new Exception(result.ErrorMessage!));
                if (classification.Class == EngineErrorClassifier.EngineErrorClass.Permanent)
                {
                    log.LogWarning(
                        "Task {TaskName} 失敗且分類為永久性錯誤（{Subkind}）— 放棄重試。原因：{Reason}",
                        task.Name, classification.Subkind, classification.Reason);
                    return new Result(attemptIndex + 1, false);
                }
            }

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
