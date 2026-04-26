using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

public sealed record RunHistoryRetentionPolicy(
    int? KeepDays,
    int? KeepLastPerTask,
    IReadOnlyDictionary<Guid, int>? PerTaskOverrides = null);

public static class RunHistoryRetention
{
    /// <summary>
    /// 依保留政策決定哪些 RunHistory 該被刪掉。
    /// - KeepDays           ：保留最近 N 天（StartedAt >= now.AddDays(-N)）
    /// - KeepLastPerTask    ：每個 EtlTaskId 保留最新 N 筆
    /// - 兩者並存：OR 語意（滿足任一條件即保留），不刪。
    /// - 兩者都 null：不刪任何。
    /// 驗證：KeepDays 必須 > 0；KeepLastPerTask 必須 > 0；違反丟 ArgumentException。
    /// 回傳：應刪除的 RunHistory.Id 清單（順序不限）。
    /// </summary>
    public static IReadOnlyList<Guid> SelectIdsToDelete(
        IEnumerable<RunHistory> events,
        RunHistoryRetentionPolicy policy,
        DateTime now)
    {
        ValidatePolicy(policy);

        if (policy.KeepDays is null && policy.KeepLastPerTask is null && (policy.PerTaskOverrides is null || policy.PerTaskOverrides.Count == 0))
            return Array.Empty<Guid>();

        var allRuns = events.ToList();
        if (allRuns.Count == 0)
            return Array.Empty<Guid>();

        var keepIds = BuildKeepSet(allRuns, policy, now);

        return allRuns
            .Where(r => !keepIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToList();
    }

    private static void ValidatePolicy(RunHistoryRetentionPolicy policy)
    {
        if (policy.KeepDays.HasValue && policy.KeepDays.Value <= 0)
            throw new ArgumentException("KeepDays must be > 0.", nameof(policy));

        if (policy.KeepLastPerTask.HasValue && policy.KeepLastPerTask.Value <= 0)
            throw new ArgumentException("KeepLastPerTask must be > 0.", nameof(policy));

        if (policy.PerTaskOverrides is not null)
        {
            foreach (var kvp in policy.PerTaskOverrides)
                if (kvp.Value <= 0)
                    throw new ArgumentException(
                        $"PerTaskOverrides[{kvp.Key}] must be > 0 (got {kvp.Value}).", nameof(policy));
        }
    }

    private static HashSet<Guid> BuildKeepSet(
        List<RunHistory> runs,
        RunHistoryRetentionPolicy policy,
        DateTime now)
    {
        var keepIds = new HashSet<Guid>();

        if (policy.KeepDays.HasValue)
        {
            var cutoff = now.AddDays(-policy.KeepDays.Value);
            foreach (var r in runs.Where(r => r.StartedAt >= cutoff))
                keepIds.Add(r.Id);
        }

        // Per-task last-N：每個 task 採用 PerTaskOverrides[taskId]（若有）優先；否則退化到全域 KeepLastPerTask。
        // 注意：若某 task 既沒 override 也沒 global → 該 task 的所有 runs 全保留（不要因為 group 沒 policy 就誤刪）。
        var hasPerTaskPolicy = policy.KeepLastPerTask.HasValue || (policy.PerTaskOverrides is not null && policy.PerTaskOverrides.Count > 0);
        if (hasPerTaskPolicy)
        {
            foreach (var group in runs.GroupBy(r => r.EtlTaskId))
            {
                int? n = null;
                if (policy.PerTaskOverrides is not null && policy.PerTaskOverrides.TryGetValue(group.Key, out var overrideN))
                    n = overrideN;
                else if (policy.KeepLastPerTask.HasValue)
                    n = policy.KeepLastPerTask.Value;

                if (n is null)
                {
                    // 沒任何政策套到此 task → 全留
                    foreach (var r in group) keepIds.Add(r.Id);
                    continue;
                }
                foreach (var r in group.OrderByDescending(r => r.StartedAt).Take(n.Value))
                    keepIds.Add(r.Id);
            }
        }

        return keepIds;
    }
}
