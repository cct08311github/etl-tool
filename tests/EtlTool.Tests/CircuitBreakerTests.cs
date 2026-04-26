using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class CircuitBreakerTests
{
    private static RunHistory R(RunStatus status, DateTime startedAt)
        => new() { Id = Guid.NewGuid(), Status = status, StartedAt = startedAt };

    [Fact]
    public void Threshold_zero_returns_false()
    {
        var runs = Enumerable.Range(0, 10).Select(i =>
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-i))).ToList();
        Assert.False(CircuitBreaker.ShouldDisable(runs, threshold: 0));
    }

    [Fact]
    public void Threshold_negative_returns_false()
    {
        var runs = new List<RunHistory> { R(RunStatus.Failed, DateTime.UtcNow) };
        Assert.False(CircuitBreaker.ShouldDisable(runs, threshold: -1));
    }

    [Fact]
    public void Empty_runs_returns_false()
    {
        Assert.False(CircuitBreaker.ShouldDisable(Array.Empty<RunHistory>(), threshold: 3));
    }

    [Fact]
    public void Insufficient_runs_returns_false()
    {
        var runs = new List<RunHistory>
        {
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-1)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-2)),
        };
        // Only 2 runs but threshold is 3 — not enough data, don't trip
        Assert.False(CircuitBreaker.ShouldDisable(runs, threshold: 3));
    }

    [Fact]
    public void Last_n_all_failed_returns_true()
    {
        var runs = new List<RunHistory>
        {
            R(RunStatus.Success, DateTime.UtcNow.AddMinutes(-10)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-3)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-2)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-1)),
        };
        Assert.True(CircuitBreaker.ShouldDisable(runs, threshold: 3));
    }

    [Fact]
    public void Recent_success_breaks_streak()
    {
        var runs = new List<RunHistory>
        {
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-4)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-3)),
            R(RunStatus.Success, DateTime.UtcNow.AddMinutes(-2)),  // breaks streak
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-1)),
        };
        // last 3 = Failed, Success, Failed — not all Failed
        Assert.False(CircuitBreaker.ShouldDisable(runs, threshold: 3));
    }

    [Fact]
    public void Older_runs_ignored_when_taking_last_n()
    {
        var runs = new List<RunHistory>
        {
            R(RunStatus.Success, DateTime.UtcNow.AddMinutes(-100)),
            R(RunStatus.Success, DateTime.UtcNow.AddMinutes(-90)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-3)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-2)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-1)),
        };
        // 5 runs total, last 3 are Failed → trip
        Assert.True(CircuitBreaker.ShouldDisable(runs, threshold: 3));
    }

    [Fact]
    public void Running_status_does_not_count_as_failed()
    {
        var runs = new List<RunHistory>
        {
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-3)),
            R(RunStatus.Failed, DateTime.UtcNow.AddMinutes(-2)),
            R(RunStatus.Running, DateTime.UtcNow.AddMinutes(-1)),  // in flight
        };
        // last 3 = Running mixed in — not all Failed
        Assert.False(CircuitBreaker.ShouldDisable(runs, threshold: 3));
    }

    [Fact]
    public void Threshold_one_trips_on_single_failure()
    {
        var runs = new List<RunHistory> { R(RunStatus.Failed, DateTime.UtcNow) };
        Assert.True(CircuitBreaker.ShouldDisable(runs, threshold: 1));
    }

    [Theory]
    [InlineData(null, null, 0)]                  // both null → 0 (disabled)
    [InlineData(0, null, 0)]                     // explicit 0 → 0 (disabled)
    [InlineData(null, 0, 0)]                     // global 0 → 0 (disabled)
    [InlineData(5, null, 5)]                     // per-task only
    [InlineData(null, 3, 3)]                     // global only
    [InlineData(5, 3, 5)]                        // per-task overrides global
    [InlineData(0, 3, 3)]                        // per-task=0 falls through to global
    [InlineData(-1, 3, 3)]                       // negative per-task ignored
    public void ResolveThreshold_precedence(int? perTask, int? global, int expected)
    {
        Assert.Equal(expected, CircuitBreaker.ResolveThreshold(perTask, global));
    }

    [Fact]
    public void Order_does_not_matter_for_input()
    {
        // Caller may pass runs in any order; helper sorts internally
        var t = DateTime.UtcNow;
        var runs = new List<RunHistory>
        {
            R(RunStatus.Failed, t.AddMinutes(-1)),
            R(RunStatus.Failed, t.AddMinutes(-3)),
            R(RunStatus.Success, t.AddMinutes(-100)),
            R(RunStatus.Failed, t.AddMinutes(-2)),
        };
        Assert.True(CircuitBreaker.ShouldDisable(runs, threshold: 3));
    }
}
