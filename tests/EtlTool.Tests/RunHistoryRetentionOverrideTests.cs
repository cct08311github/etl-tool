using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class RunHistoryRetentionOverrideTests
{
    private static RunHistory R(Guid taskId, DateTime startedAt, RunStatus status = RunStatus.Success)
        => new() { Id = Guid.NewGuid(), EtlTaskId = taskId, StartedAt = startedAt, Status = status };

    [Fact]
    public void Per_task_override_takes_precedence_over_global()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);

        // 各 task 各 10 筆
        var runs = new List<RunHistory>();
        for (int i = 0; i < 10; i++)
        {
            runs.Add(R(t1, now.AddDays(-i)));
            runs.Add(R(t2, now.AddDays(-i)));
        }

        // Global 保留 3，但 t1 覆寫成保留 5
        var policy = new RunHistoryRetentionPolicy(
            KeepDays: null, KeepLastPerTask: 3,
            PerTaskOverrides: new Dictionary<Guid, int> { [t1] = 5 });

        var toDelete = RunHistoryRetention.SelectIdsToDelete(runs, policy, now);

        // t1 應該保留 5 筆 → 刪 5；t2 保留 3 → 刪 7
        var t1Deleted = runs.Where(r => r.EtlTaskId == t1 && toDelete.Contains(r.Id)).Count();
        var t2Deleted = runs.Where(r => r.EtlTaskId == t2 && toDelete.Contains(r.Id)).Count();
        Assert.Equal(5, t1Deleted);
        Assert.Equal(7, t2Deleted);
    }

    [Fact]
    public void Per_task_override_keeps_newest_n_runs()
    {
        var t1 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);

        var runs = new List<RunHistory>
        {
            R(t1, now.AddDays(-1)),
            R(t1, now.AddDays(-2)),
            R(t1, now.AddDays(-3)),
            R(t1, now.AddDays(-4)),
            R(t1, now.AddDays(-5)),
        };

        var policy = new RunHistoryRetentionPolicy(
            null, null,
            new Dictionary<Guid, int> { [t1] = 2 });

        var toDelete = RunHistoryRetention.SelectIdsToDelete(runs, policy, now).ToHashSet();

        // 保留最近 2 筆 (-1 day, -2 day) → 刪 -3, -4, -5 day
        Assert.Equal(3, toDelete.Count);
        Assert.False(toDelete.Contains(runs[0].Id));  // -1
        Assert.False(toDelete.Contains(runs[1].Id));  // -2
        Assert.True(toDelete.Contains(runs[2].Id));   // -3
        Assert.True(toDelete.Contains(runs[3].Id));   // -4
        Assert.True(toDelete.Contains(runs[4].Id));   // -5
    }

    [Fact]
    public void Override_only_with_no_global_does_not_affect_unlisted_tasks()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);

        var runs = new List<RunHistory>
        {
            R(t1, now.AddDays(-1)), R(t1, now.AddDays(-2)), R(t1, now.AddDays(-3)),
            R(t2, now.AddDays(-1)), R(t2, now.AddDays(-2)), R(t2, now.AddDays(-3)),
        };

        // 只有 t1 有 override (=1)；t2 沒列出 → 不應有任何 t2 被刪
        var policy = new RunHistoryRetentionPolicy(
            null, null,
            new Dictionary<Guid, int> { [t1] = 1 });

        var toDelete = RunHistoryRetention.SelectIdsToDelete(runs, policy, now).ToHashSet();

        var t1Deleted = runs.Where(r => r.EtlTaskId == t1 && toDelete.Contains(r.Id)).Count();
        var t2Deleted = runs.Where(r => r.EtlTaskId == t2 && toDelete.Contains(r.Id)).Count();
        Assert.Equal(2, t1Deleted);  // t1 留 1 → 刪 2
        Assert.Equal(0, t2Deleted);  // t2 沒列、沒全域 → 全留
    }

    [Fact]
    public void Override_combined_with_keep_days_uses_OR_semantics()
    {
        var t1 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);

        // 3 筆 < 7 天前；3 筆 > 30 天前
        var runs = new List<RunHistory>
        {
            R(t1, now.AddDays(-1)),
            R(t1, now.AddDays(-3)),
            R(t1, now.AddDays(-5)),
            R(t1, now.AddDays(-40)),
            R(t1, now.AddDays(-50)),
            R(t1, now.AddDays(-60)),
        };

        // Override = 1（只留最新 1 筆）+ KeepDays = 7
        // KeepDays=7 留 -1/-3/-5；Override=1 留 -1。OR → 留 -1, -3, -5；刪 -40/-50/-60。
        var policy = new RunHistoryRetentionPolicy(
            KeepDays: 7, KeepLastPerTask: null,
            PerTaskOverrides: new Dictionary<Guid, int> { [t1] = 1 });

        var toDelete = RunHistoryRetention.SelectIdsToDelete(runs, policy, now).ToHashSet();
        Assert.Equal(3, toDelete.Count);
        // 確認：留下的 3 筆是 -1/-3/-5
        var keptIds = runs.Where(r => !toDelete.Contains(r.Id)).Select(r => r.Id).ToList();
        Assert.Equal(3, keptIds.Count);
    }

    [Fact]
    public void Negative_or_zero_override_throws()
    {
        var t1 = Guid.NewGuid();
        var policy = new RunHistoryRetentionPolicy(
            null, null,
            new Dictionary<Guid, int> { [t1] = 0 });

        Assert.Throws<ArgumentException>(() =>
            RunHistoryRetention.SelectIdsToDelete(Array.Empty<RunHistory>(), policy, DateTime.UtcNow));
    }

    [Fact]
    public void Empty_overrides_dict_falls_back_to_global()
    {
        var t1 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc);
        var runs = new List<RunHistory>
        {
            R(t1, now.AddDays(-1)), R(t1, now.AddDays(-2)),
            R(t1, now.AddDays(-3)), R(t1, now.AddDays(-4)),
        };

        // 空 overrides + global=2
        var policy = new RunHistoryRetentionPolicy(
            null, KeepLastPerTask: 2,
            PerTaskOverrides: new Dictionary<Guid, int>());

        var toDelete = RunHistoryRetention.SelectIdsToDelete(runs, policy, now).ToHashSet();
        Assert.Equal(2, toDelete.Count);  // 刪最舊 2 筆
    }

    [Fact]
    public void All_null_returns_empty()
    {
        var runs = new List<RunHistory> { R(Guid.NewGuid(), DateTime.UtcNow) };
        var policy = new RunHistoryRetentionPolicy(null, null, null);
        var toDelete = RunHistoryRetention.SelectIdsToDelete(runs, policy, DateTime.UtcNow);
        Assert.Empty(toDelete);
    }
}
