using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

public sealed record AuditRetentionPolicy(int? KeepDays, int? KeepLastPerCategory);

public static class AuditRetention
{
    /// <summary>
    /// 依保留政策決定哪些 AuditEvent 該被刪掉。
    /// 兩個欄位的語意：
    ///   - KeepDays           ：保留最近 N 天（At >= now.AddDays(-N)）
    ///   - KeepLastPerCategory：每個 Category 保留最新 N 筆
    ///   - 兩者並存：OR 語意（滿足任一條件即保留），不刪。
    ///   - 兩者都 null：不刪任何。
    /// 驗證：KeepDays 必須 > 0；KeepLastPerCategory 必須 > 0；違反丟 ArgumentException。
    /// 回傳：應刪除的 Id 清單（順序不限）。
    /// </summary>
    public static IReadOnlyList<Guid> SelectIdsToDelete(
        IEnumerable<AuditEvent> events,
        AuditRetentionPolicy policy,
        DateTime now)
    {
        ValidatePolicy(policy);

        if (policy.KeepDays is null && policy.KeepLastPerCategory is null)
            return Array.Empty<Guid>();

        var allEvents = events.ToList();
        if (allEvents.Count == 0)
            return Array.Empty<Guid>();

        var keepIds = BuildKeepSet(allEvents, policy, now);

        return allEvents
            .Where(e => !keepIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToList();
    }

    private static void ValidatePolicy(AuditRetentionPolicy policy)
    {
        if (policy.KeepDays.HasValue && policy.KeepDays.Value <= 0)
            throw new ArgumentException("KeepDays must be > 0.", nameof(policy));

        if (policy.KeepLastPerCategory.HasValue && policy.KeepLastPerCategory.Value <= 0)
            throw new ArgumentException("KeepLastPerCategory must be > 0.", nameof(policy));
    }

    private static HashSet<Guid> BuildKeepSet(
        List<AuditEvent> events,
        AuditRetentionPolicy policy,
        DateTime now)
    {
        var keepIds = new HashSet<Guid>();

        if (policy.KeepDays.HasValue)
        {
            var cutoff = now.AddDays(-policy.KeepDays.Value);
            foreach (var e in events.Where(e => e.At >= cutoff))
                keepIds.Add(e.Id);
        }

        if (policy.KeepLastPerCategory.HasValue)
        {
            var n = policy.KeepLastPerCategory.Value;
            foreach (var group in events.GroupBy(e => e.Category))
            {
                foreach (var e in group.OrderByDescending(e => e.At).Take(n))
                    keepIds.Add(e.Id);
            }
        }

        return keepIds;
    }
}
