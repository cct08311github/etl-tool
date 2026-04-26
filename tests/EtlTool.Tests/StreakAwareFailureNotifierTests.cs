using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class StreakAwareFailureNotifierTests
{
    private sealed class CountingNotifier : IFailureNotifier
    {
        public int FailureCalls { get; private set; }
        public int OutcomeCalls { get; private set; }
        public List<(string Status, string? ErrorMsg)> Received { get; } = new();

        public Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct)
        {
            FailureCalls++;
            return Task.CompletedTask;
        }

        public Task NotifyRunOutcomeAsync(EtlTask task, RunHistory run, CancellationToken ct)
        {
            OutcomeCalls++;
            Received.Add((run.Status.ToString(), run.ErrorMessage));
            return Task.CompletedTask;
        }
    }

    private static EtlTask MakeTask(Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), Name = "T" };

    private static RunHistory MakeRun(RunStatus status, string? error = null)
        => new()
        {
            Id = Guid.NewGuid(),
            EtlTaskId = Guid.Empty,
            Status = status,
            StartedAt = DateTime.UtcNow,
            ErrorMessage = error,
        };

    [Fact]
    public async Task First_failure_below_threshold_does_not_notify()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 3);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed, "boom"), default);

        Assert.Equal(0, inner.OutcomeCalls);
        var s = n.GetState(task.Id);
        Assert.Equal(1, s.ConsecutiveFailures);
        Assert.False(s.HasAlerted);
    }

    [Fact]
    public async Task At_threshold_fires_once()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 3);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        Assert.Equal(0, inner.OutcomeCalls);
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed, "third"), default);
        Assert.Equal(1, inner.OutcomeCalls);
        Assert.Contains("[STREAK 3/3]", inner.Received[^1].ErrorMsg ?? "");
    }

    [Fact]
    public async Task Above_threshold_does_not_repeat()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 2);
        var task = MakeTask();

        for (int i = 0; i < 10; i++)
            await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);

        // 1 fail (below) + 1 fail (at threshold → fire) + 8 fails (above → silent)
        Assert.Equal(1, inner.OutcomeCalls);
        var s = n.GetState(task.Id);
        Assert.Equal(10, s.ConsecutiveFailures);
        Assert.True(s.HasAlerted);
    }

    [Fact]
    public async Task Recovery_after_alert_fires_recovery_notification()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 2, recoveryNotifications: true);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default); // alert fires
        Assert.Equal(1, inner.OutcomeCalls);

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Success), default);

        Assert.Equal(2, inner.OutcomeCalls);
        Assert.Contains("RECOVERY", inner.Received[^1].ErrorMsg ?? "");
        var s = n.GetState(task.Id);
        Assert.Equal(0, s.ConsecutiveFailures);
        Assert.False(s.HasAlerted);
    }

    [Fact]
    public async Task Recovery_when_never_alerted_is_silent()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 5);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Success), default);

        // 從來沒到過門檻 → 即使 success 也不打擾
        Assert.Equal(0, inner.OutcomeCalls);
    }

    [Fact]
    public async Task Recovery_disabled_does_not_fire_on_success()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 2, recoveryNotifications: false);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);  // alert
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Success), default); // would-be recovery, suppressed

        Assert.Equal(1, inner.OutcomeCalls);
    }

    [Fact]
    public async Task State_is_per_task()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 2);
        var t1 = MakeTask();
        var t2 = MakeTask();

        await n.NotifyRunOutcomeAsync(t1, MakeRun(RunStatus.Failed), default);
        await n.NotifyRunOutcomeAsync(t1, MakeRun(RunStatus.Failed), default); // t1 alert
        await n.NotifyRunOutcomeAsync(t2, MakeRun(RunStatus.Failed), default); // t2 only 1
        Assert.Equal(1, inner.OutcomeCalls);
        Assert.Equal(2, n.GetState(t1.Id).ConsecutiveFailures);
        Assert.Equal(1, n.GetState(t2.Id).ConsecutiveFailures);
    }

    [Fact]
    public async Task Streak_resets_on_intermediate_success()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 3);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        // Got close but recovered before alerting
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Success), default);
        // Now start streak fresh
        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        Assert.Equal(1, n.GetState(task.Id).ConsecutiveFailures);
        Assert.Equal(0, inner.OutcomeCalls);
    }

    [Fact]
    public void Threshold_zero_or_negative_throws()
    {
        var inner = new CountingNotifier();
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreakAwareFailureNotifier(inner, threshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreakAwareFailureNotifier(inner, threshold: -1));
    }

    [Fact]
    public async Task Threshold_one_fires_immediately_on_first_failure()
    {
        var inner = new CountingNotifier();
        var n = new StreakAwareFailureNotifier(inner, threshold: 1);
        var task = MakeTask();

        await n.NotifyRunOutcomeAsync(task, MakeRun(RunStatus.Failed), default);
        Assert.Equal(1, inner.OutcomeCalls);
        Assert.Contains("[STREAK 1/1]", inner.Received[^1].ErrorMsg ?? "");
    }
}
