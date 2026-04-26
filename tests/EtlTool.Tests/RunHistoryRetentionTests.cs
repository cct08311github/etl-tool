using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class RunHistoryRetentionTests
{
    private static readonly DateTime Now = new DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc);

    private static RunHistory MakeRun(DateTime startedAt, Guid? taskId = null)
        => new RunHistory
        {
            Id = Guid.NewGuid(),
            EtlTaskId = taskId ?? Guid.NewGuid(),
            StartedAt = startedAt,
            Status = RunStatus.Success,
        };

    // Case 1: Both policy fields null → empty list (delete nothing)
    [Fact]
    public void BothNull_ReturnsEmpty()
    {
        var runs = new[]
        {
            MakeRun(Now.AddDays(-100)),
            MakeRun(Now.AddDays(-1)),
        };
        var policy = new RunHistoryRetentionPolicy(null, null);
        var result = RunHistoryRetention.SelectIdsToDelete(runs, policy, Now);
        Assert.Empty(result);
    }

    // Case 2: KeepDays=7 → 8 days old deleted, 6 days old kept (same task)
    [Fact]
    public void KeepDays7_DeletesOldOne()
    {
        var taskId = Guid.NewGuid();
        var old = MakeRun(Now.AddDays(-8), taskId);
        var recent = MakeRun(Now.AddDays(-6), taskId);
        var policy = new RunHistoryRetentionPolicy(7, null);
        var result = RunHistoryRetention.SelectIdsToDelete(new[] { old, recent }, policy, Now);
        Assert.Equal(new[] { old.Id }, result.OrderBy(x => x).ToArray());
    }

    // Case 3: KeepDays=7 boundary — exactly 7 days → kept; 7 days + 1ms → deleted
    [Fact]
    public void KeepDays7_Boundary()
    {
        var exactBoundary = MakeRun(Now.AddDays(-7));       // StartedAt == cutoff → keep
        var justOver = MakeRun(Now.AddDays(-7).AddMilliseconds(-1)); // StartedAt < cutoff → delete
        var policy = new RunHistoryRetentionPolicy(7, null);
        var result = RunHistoryRetention.SelectIdsToDelete(new[] { exactBoundary, justOver }, policy, Now);
        Assert.Contains(justOver.Id, result);
        Assert.DoesNotContain(exactBoundary.Id, result);
    }

    // Case 4: KeepLastPerTask=2, same task 5 runs → delete 3 oldest
    [Fact]
    public void KeepLastPerTask2_SameTask_DeletesThreeOldest()
    {
        var taskId = Guid.NewGuid();
        var runs = Enumerable.Range(1, 5)
            .Select(i => MakeRun(Now.AddDays(-i), taskId))
            .ToArray();
        // runs[0] = 1 day ago (newest), runs[4] = 5 days ago (oldest)
        var policy = new RunHistoryRetentionPolicy(null, 2);
        var result = RunHistoryRetention.SelectIdsToDelete(runs, policy, Now);
        Assert.Equal(3, result.Count);
        var deletedIds = result.ToHashSet();
        Assert.Contains(runs[2].Id, deletedIds);
        Assert.Contains(runs[3].Id, deletedIds);
        Assert.Contains(runs[4].Id, deletedIds);
        Assert.DoesNotContain(runs[0].Id, deletedIds);
        Assert.DoesNotContain(runs[1].Id, deletedIds);
    }

    // Case 5: KeepLastPerTask=2, cross task (A:3, B:1) → only delete A's oldest, keep B
    [Fact]
    public void KeepLastPerTask2_CrossTask()
    {
        var taskA = Guid.NewGuid();
        var taskB = Guid.NewGuid();
        var a1 = MakeRun(Now.AddDays(-1), taskA);
        var a2 = MakeRun(Now.AddDays(-2), taskA);
        var a3 = MakeRun(Now.AddDays(-3), taskA);
        var b1 = MakeRun(Now.AddDays(-5), taskB);
        var policy = new RunHistoryRetentionPolicy(null, 2);
        var result = RunHistoryRetention.SelectIdsToDelete(new[] { a1, a2, a3, b1 }, policy, Now);
        Assert.Single(result);
        Assert.Contains(a3.Id, result);
        Assert.DoesNotContain(b1.Id, result);
    }

    // Case 6: OR semantics — 30 days old but among newest 2 per task → kept
    [Fact]
    public void OrSemantics_OldButInTopN_Kept()
    {
        var taskId = Guid.NewGuid();
        // Only 2 runs for the task, both 30+ days old; KeepDays=7 would delete both,
        // but KeepLastPerTask=2 saves both (they are the top 2).
        var e1 = MakeRun(Now.AddDays(-30), taskId);
        var e2 = MakeRun(Now.AddDays(-25), taskId);
        var policy = new RunHistoryRetentionPolicy(7, 2);
        var result = RunHistoryRetention.SelectIdsToDelete(new[] { e1, e2 }, policy, Now);
        Assert.Empty(result);
    }

    // Case 7: Invalid policy values → ArgumentException
    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    public void InvalidPolicy_ThrowsArgumentException(int? keepDays, int? keepLastPerTask)
    {
        var policy = new RunHistoryRetentionPolicy(keepDays, keepLastPerTask);
        Assert.Throws<ArgumentException>(() =>
            RunHistoryRetention.SelectIdsToDelete(Array.Empty<RunHistory>(), policy, Now));
    }

    // Case 8: Empty collection + any valid policy → empty list
    [Theory]
    [InlineData(7, null)]
    [InlineData(null, 3)]
    [InlineData(30, 10)]
    public void EmptyEvents_ReturnsEmpty(int? keepDays, int? keepLastPerTask)
    {
        var policy = new RunHistoryRetentionPolicy(keepDays, keepLastPerTask);
        var result = RunHistoryRetention.SelectIdsToDelete(Array.Empty<RunHistory>(), policy, Now);
        Assert.Empty(result);
    }
}
