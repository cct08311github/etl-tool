using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlTool.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task No_retry_when_first_attempt_succeeds()
    {
        var calls = new List<TriggerType>();
        var task = NewTask(maxRetries: 3);

        var result = await RetryPolicy.RunWithRetriesAsync(
            task, TriggerType.Manual,
            attempt: (trig, ct) => { calls.Add(trig); return Task.FromResult(RunStatus.Success); },
            NullLogger.Instance, default,
            sleeper: NoSleep);

        Assert.Equal(1, result.TotalAttempts);
        Assert.True(result.FinalSucceeded);
        Assert.Equal(new[] { TriggerType.Manual }, calls);
    }

    [Fact]
    public async Task Retries_until_success_then_stops()
    {
        var calls = new List<TriggerType>();
        var task = NewTask(maxRetries: 5);
        int n = 0;

        var result = await RetryPolicy.RunWithRetriesAsync(
            task, TriggerType.Scheduled,
            attempt: (trig, ct) =>
            {
                calls.Add(trig);
                n++;
                return Task.FromResult(n >= 3 ? RunStatus.Success : RunStatus.Failed);
            },
            NullLogger.Instance, default,
            sleeper: NoSleep);

        Assert.Equal(3, result.TotalAttempts);
        Assert.True(result.FinalSucceeded);
        Assert.Equal(new[] { TriggerType.Scheduled, TriggerType.Retry, TriggerType.Retry }, calls);
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var calls = 0;
        var task = NewTask(maxRetries: 2);

        var result = await RetryPolicy.RunWithRetriesAsync(
            task, TriggerType.Scheduled,
            attempt: (trig, ct) => { calls++; return Task.FromResult(RunStatus.Failed); },
            NullLogger.Instance, default,
            sleeper: NoSleep);

        Assert.Equal(3, calls);                     // 1 initial + 2 retries
        Assert.Equal(3, result.TotalAttempts);
        Assert.False(result.FinalSucceeded);
    }

    [Fact]
    public async Task MaxRetries_zero_means_no_retry()
    {
        var calls = 0;
        var task = NewTask(maxRetries: 0);

        var result = await RetryPolicy.RunWithRetriesAsync(
            task, TriggerType.Scheduled,
            attempt: (trig, ct) => { calls++; return Task.FromResult(RunStatus.Failed); },
            NullLogger.Instance, default,
            sleeper: NoSleep);

        Assert.Equal(1, calls);
        Assert.False(result.FinalSucceeded);
    }

    [Fact]
    public async Task Backoff_doubles_delay_each_attempt()
    {
        var delays = new List<double>();
        var task = NewTask(maxRetries: 3, delaySec: 5, multiplier: 2.0);
        int n = 0;

        await RetryPolicy.RunWithRetriesAsync(
            task, TriggerType.Scheduled,
            attempt: (trig, ct) => { n++; return Task.FromResult(RunStatus.Failed); },
            NullLogger.Instance, default,
            sleeper: (delay, ct) => { delays.Add(delay.TotalSeconds); return Task.CompletedTask; });

        // 3 retries → 3 sleeps; backoff: 5s, 10s, 20s
        Assert.Equal(new[] { 5.0, 10.0, 20.0 }, delays);
    }

    [Fact]
    public async Task Cancellation_during_sleep_stops_loop()
    {
        var calls = 0;
        var task = NewTask(maxRetries: 3, delaySec: 5);
        var cts = new CancellationTokenSource();

        var result = await RetryPolicy.RunWithRetriesAsync(
            task, TriggerType.Scheduled,
            attempt: (trig, ct) => { calls++; return Task.FromResult(RunStatus.Failed); },
            NullLogger.Instance, cts.Token,
            sleeper: (delay, ct) => { cts.Cancel(); throw new OperationCanceledException(); });

        Assert.Equal(1, calls);
        Assert.False(result.FinalSucceeded);
    }

    private static EtlTask NewTask(int maxRetries, int delaySec = 1, double multiplier = 1.0) => new()
    {
        Name = "T",
        MaxRetries = maxRetries,
        RetryDelaySeconds = delaySec,
        RetryBackoffMultiplier = multiplier,
    };

    private static Task NoSleep(TimeSpan _, CancellationToken __) => Task.CompletedTask;
}
