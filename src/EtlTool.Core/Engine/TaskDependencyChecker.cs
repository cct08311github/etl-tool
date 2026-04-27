using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 純函式 — 給定一個 task 的 dependsOnIds + 一份「per-task 最近 Success run 時間」字典，
/// 判斷依賴是否全部滿足（在 lookback window 內）。
/// </summary>
public static class TaskDependencyChecker
{
    public sealed record DependencyCheckResult(
        bool AllSatisfied,
        IReadOnlyList<Guid> UnsatisfiedParentIds,
        string? Reason);

    /// <summary>
    /// 解析逗號分隔 GUID 字串。空字串 / null = 無依賴。
    /// 無效 GUID 會被跳過（不會丟例外，僅靜默忽略）。
    /// </summary>
    public static IReadOnlyList<Guid> ParseDependsOnIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Guid>();
        var result = new List<Guid>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(part.Trim(), out var g))
                result.Add(g);
        }
        return result;
    }

    /// <summary>
    /// 檢查依賴。lookbackHours：parent 必須在過去 N 小時內有 Success；預設 24h。
    /// lookbackHours &lt;= 0 → 視為「any time」（曾經成功過即可）。
    /// </summary>
    public static DependencyCheckResult CheckDependencies(
        IReadOnlyList<Guid> dependsOnIds,
        IReadOnlyDictionary<Guid, DateTime> lastSuccessByTask,
        DateTime now,
        int lookbackHours = 24)
    {
        if (dependsOnIds.Count == 0)
            return new DependencyCheckResult(true, Array.Empty<Guid>(), null);

        var unsatisfied = new List<Guid>();
        DateTime? cutoff = lookbackHours > 0 ? now.AddHours(-lookbackHours) : null;

        foreach (var pid in dependsOnIds)
        {
            if (!lastSuccessByTask.TryGetValue(pid, out var lastAt))
            {
                unsatisfied.Add(pid);
                continue;
            }
            if (cutoff is not null && lastAt < cutoff.Value)
                unsatisfied.Add(pid);
        }

        if (unsatisfied.Count == 0)
            return new DependencyCheckResult(true, Array.Empty<Guid>(), null);

        var reason = lookbackHours > 0
            ? $"上游任務未在過去 {lookbackHours} 小時內成功（{unsatisfied.Count} 個 parent 未滿足）"
            : $"上游任務從未成功（{unsatisfied.Count} 個 parent 未滿足）";
        return new DependencyCheckResult(false, unsatisfied, reason);
    }
}
