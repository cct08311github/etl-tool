using EtlTool.Core.Engine;

namespace EtlTool.Tests;

public class TaskDependencyCheckerTests
{
    [Fact]
    public void ParseDependsOnIds_empty_or_null_returns_empty()
    {
        Assert.Empty(TaskDependencyChecker.ParseDependsOnIds(null));
        Assert.Empty(TaskDependencyChecker.ParseDependsOnIds(""));
        Assert.Empty(TaskDependencyChecker.ParseDependsOnIds(" "));
    }

    [Fact]
    public void ParseDependsOnIds_handles_valid_guids()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var raw = $"{g1},{g2}";
        var ids = TaskDependencyChecker.ParseDependsOnIds(raw);
        Assert.Equal(2, ids.Count);
        Assert.Contains(g1, ids);
        Assert.Contains(g2, ids);
    }

    [Fact]
    public void ParseDependsOnIds_skips_invalid_silently()
    {
        var g = Guid.NewGuid();
        var ids = TaskDependencyChecker.ParseDependsOnIds($"not-a-guid,{g},also-bad");
        Assert.Single(ids);
        Assert.Equal(g, ids[0]);
    }

    [Fact]
    public void ParseDependsOnIds_trims_whitespace()
    {
        var g = Guid.NewGuid();
        var ids = TaskDependencyChecker.ParseDependsOnIds($"  {g}  ");
        Assert.Single(ids);
    }

    [Fact]
    public void CheckDependencies_empty_dependencies_always_passes()
    {
        var result = TaskDependencyChecker.CheckDependencies(
            Array.Empty<Guid>(),
            new Dictionary<Guid, DateTime>(),
            DateTime.UtcNow);
        Assert.True(result.AllSatisfied);
        Assert.Empty(result.UnsatisfiedParentIds);
    }

    [Fact]
    public void CheckDependencies_all_parents_recently_succeeded_passes()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
        var lastSuccess = new Dictionary<Guid, DateTime>
        {
            [p1] = now.AddHours(-1),
            [p2] = now.AddHours(-2),
        };
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1, p2 }, lastSuccess, now, lookbackHours: 24);
        Assert.True(result.AllSatisfied);
    }

    [Fact]
    public void CheckDependencies_parent_never_succeeded_fails()
    {
        var p1 = Guid.NewGuid();
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1 },
            new Dictionary<Guid, DateTime>(),
            DateTime.UtcNow);
        Assert.False(result.AllSatisfied);
        Assert.Contains(p1, result.UnsatisfiedParentIds);
        Assert.Contains("未在過去 24 小時內成功", result.Reason!);
    }

    [Fact]
    public void CheckDependencies_parent_outside_lookback_fails()
    {
        var p1 = Guid.NewGuid();
        var now = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
        var lastSuccess = new Dictionary<Guid, DateTime> { [p1] = now.AddHours(-25) };
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1 }, lastSuccess, now, lookbackHours: 24);
        Assert.False(result.AllSatisfied);
        Assert.Contains(p1, result.UnsatisfiedParentIds);
    }

    [Fact]
    public void CheckDependencies_partial_satisfaction_lists_unmet()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p3 = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var lastSuccess = new Dictionary<Guid, DateTime>
        {
            [p1] = now.AddHours(-1),    // ok
            [p2] = now.AddHours(-30),   // too old
            // p3 not in dict at all
        };
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1, p2, p3 }, lastSuccess, now, lookbackHours: 24);
        Assert.False(result.AllSatisfied);
        Assert.Equal(2, result.UnsatisfiedParentIds.Count);
        Assert.Contains(p2, result.UnsatisfiedParentIds);
        Assert.Contains(p3, result.UnsatisfiedParentIds);
        Assert.DoesNotContain(p1, result.UnsatisfiedParentIds);
    }

    [Fact]
    public void CheckDependencies_zero_lookback_means_any_time()
    {
        var p1 = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // Parent succeeded 1 year ago — in normal mode would fail
        var lastSuccess = new Dictionary<Guid, DateTime>
        {
            [p1] = now.AddDays(-365),
        };
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1 }, lastSuccess, now, lookbackHours: 0);
        Assert.True(result.AllSatisfied);
    }

    [Fact]
    public void CheckDependencies_zero_lookback_still_fails_if_never_succeeded()
    {
        var p1 = Guid.NewGuid();
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1 },
            new Dictionary<Guid, DateTime>(),
            DateTime.UtcNow,
            lookbackHours: 0);
        Assert.False(result.AllSatisfied);
        Assert.Contains("從未成功", result.Reason!);
    }

    [Fact]
    public void CheckDependencies_negative_lookback_treated_as_any_time()
    {
        var p1 = Guid.NewGuid();
        var lastSuccess = new Dictionary<Guid, DateTime> { [p1] = DateTime.UtcNow.AddYears(-1) };
        var result = TaskDependencyChecker.CheckDependencies(
            new[] { p1 }, lastSuccess, DateTime.UtcNow, lookbackHours: -1);
        Assert.True(result.AllSatisfied);
    }
}
