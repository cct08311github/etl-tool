using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// ETL 執行結果通知通道。實作可以是 webhook、email、log-only。
/// 設計：fire-and-forget 風格，回傳前必須 catch 所有例外（不可影響呼叫端）。
///
/// EtlEngine 在每次 run 完成（成功或失敗）時呼叫 NotifyRunOutcomeAsync。
/// 預設 Default impl：失敗就轉呼叫 NotifyFailureAsync；成功不通知。
/// 可透過 decorator (e.g. StreakAwareFailureNotifier) 加入「連續失敗才通知」/
/// 「恢復才通知」之類更聰明的策略。
/// </summary>
public interface IFailureNotifier
{
    /// <summary>已知失敗的單次通知（保留為向後相容介面）。</summary>
    Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct);

    /// <summary>
    /// 通用 outcome 通知。預設實作：Failed → NotifyFailureAsync，其他 → no-op。
    /// 包成 default method 是為了不破壞既有實作，但建議新實作直接覆寫此方法。
    /// </summary>
    Task NotifyRunOutcomeAsync(EtlTask task, RunHistory run, CancellationToken ct)
    {
        if (run.Status == RunStatus.Failed)
            return NotifyFailureAsync(task, run, ct);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 不做任何事的預設實作。Production 環境用 HTTP webhook 取代。
/// </summary>
public sealed class NoopFailureNotifier : IFailureNotifier
{
    public Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct) => Task.CompletedTask;
}
