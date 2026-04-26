using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// EtlJob 執行完一次後呼叫；實作層決定是否要 auto-disable 任務。
/// 在 Core 中以介面定義，避免 Core 反向依賴 Data / App。
/// </summary>
public interface ICircuitBreakerEnforcer
{
    Task OnRunCompleteAsync(EtlTask task, RunHistory run, CancellationToken ct);
}

/// <summary>
/// 純函式 circuit-breaker：給定一連串 RunHistory（含本次最新一筆）、threshold，
/// 判斷是否應該停用此任務。
///
/// 規則：
///   - threshold &lt;= 0 → 停用 circuit-breaker（永遠回 false）
///   - 取最近 threshold 筆（OrderByDesc StartedAt）
///   - 不滿 threshold 筆 → 不觸發（資料不足）
///   - 全為 RunStatus.Failed → 觸發
///
/// 不關心 TriggerType — manual / scheduled / retry 任何失敗都計數。
/// 想對 retry 不算另計，可在呼叫前先過濾。
/// </summary>
public static class CircuitBreaker
{
    public static bool ShouldDisable(IEnumerable<RunHistory> recentRuns, int threshold)
    {
        if (threshold <= 0) return false;

        var lastN = recentRuns
            .OrderByDescending(r => r.StartedAt)
            .Take(threshold)
            .ToList();

        if (lastN.Count < threshold) return false;
        return lastN.All(r => r.Status == RunStatus.Failed);
    }

    /// <summary>
    /// 解析 task 的 AutoDisableAfterFailures override 與全域預設，回傳實際 threshold。
    /// 0 / null = disabled（回 0）。
    /// </summary>
    public static int ResolveThreshold(int? perTaskOverride, int? globalDefault)
    {
        if (perTaskOverride is { } po && po > 0) return po;
        if (globalDefault is { } g && g > 0) return g;
        return 0;
    }
}
