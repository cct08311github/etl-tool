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
    public sealed record CycleDetectionResult(
        bool HasCycle,
        IReadOnlyList<Guid>? CyclePath,
        string? Reason);

    /// <summary>
    /// 偵測是否「為 candidateTaskId 加上這些 parents」會造成循環。
    /// allTaskDeps：所有現存 task 的 deps（含 candidate 自己若已存在）。
    /// 用 DFS 檢查每個 newParent 是否能反向走回 candidateTaskId。
    ///
    /// 簡化規則：
    ///   - 若 newParent == candidateTaskId → self-loop（直接 cycle）
    ///   - 從 newParent 出發走 allTaskDeps，若到達 candidateTaskId → cycle
    /// 回傳路徑供 UI 顯示「A→B→C→A」。
    /// </summary>
    public static CycleDetectionResult DetectCycle(
        Guid candidateTaskId,
        IReadOnlyList<Guid> newDependsOnIds,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> allTaskDeps)
    {
        foreach (var newParent in newDependsOnIds)
        {
            if (newParent == candidateTaskId)
            {
                return new CycleDetectionResult(true,
                    new[] { candidateTaskId, candidateTaskId },
                    "任務不可依賴自己");
            }

            // DFS：從 newParent 沿 allTaskDeps 走，看能否到達 candidateTaskId
            var path = new List<Guid> { newParent };
            var visited = new HashSet<Guid> { newParent };
            if (DfsReaches(newParent, candidateTaskId, allTaskDeps, visited, path))
            {
                // path 從 newParent 走到 candidateTaskId
                // 完整 cycle = candidateTaskId → newParent → ... → candidateTaskId
                var fullCycle = new List<Guid> { candidateTaskId };
                fullCycle.AddRange(path);
                return new CycleDetectionResult(true, fullCycle,
                    $"加上此依賴會形成循環：{string.Join(" → ", fullCycle)}");
            }
        }
        return new CycleDetectionResult(false, null, null);
    }

    private static bool DfsReaches(
        Guid current,
        Guid target,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> allDeps,
        HashSet<Guid> visited,
        List<Guid> path)
    {
        if (!allDeps.TryGetValue(current, out var deps)) return false;
        foreach (var d in deps)
        {
            if (d == target)
            {
                path.Add(d);
                return true;
            }
            if (!visited.Add(d)) continue;
            path.Add(d);
            if (DfsReaches(d, target, allDeps, visited, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

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
