using System.Collections.Concurrent;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 連續失敗 alerting 策略 — 把噪音降下來，banking ops 才不會 alert fatigue。
///
/// 規則：
///   - 失敗：每 task 累積 streak count
///       streak == Threshold → 觸發內部 notifier（含 streak 訊息）
///       streak  > Threshold → 不再觸發（除非 RecoveryNotificationsEnabled）
///   - 成功：
///       上次有觸發過 alert → 觸發「recovery」通知 + reset state
///       上次沒觸發過 → 只是 reset state，不打擾人
///
/// 狀態 in-memory，per-process。重啟後重新累積（保守：避免 false-positive 連帶
/// 通知；若 task 一直失敗，重啟後第一次失敗仍視為 streak=1）。
///
/// 相依：注入「真正去打 webhook」的 inner IFailureNotifier，
///       並把 streak 訊息合成在 RunHistory.ErrorMessage 前綴中（例：「[STREAK 3] ...」）。
/// </summary>
public sealed class StreakAwareFailureNotifier : IFailureNotifier
{
    private readonly IFailureNotifier _inner;
    public int Threshold { get; }
    public bool RecoveryNotificationsEnabled { get; }

    private readonly ConcurrentDictionary<Guid, TaskState> _state = new();

    public StreakAwareFailureNotifier(IFailureNotifier inner, int threshold = 3, bool recoveryNotifications = true)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold), "threshold must be >= 1");
        _inner = inner;
        Threshold = threshold;
        RecoveryNotificationsEnabled = recoveryNotifications;
    }

    /// <summary>給單元測試用：取得目前的 streak 狀態。</summary>
    public TaskState GetState(Guid taskId) => _state.TryGetValue(taskId, out var s) ? s : default;

    public Task NotifyFailureAsync(EtlTask task, RunHistory run, CancellationToken ct)
        => NotifyRunOutcomeAsync(task, run, ct);

    public async Task NotifyRunOutcomeAsync(EtlTask task, RunHistory run, CancellationToken ct)
    {
        var current = _state.GetValueOrDefault(task.Id);

        if (run.Status == RunStatus.Failed)
        {
            var newStreak = current.ConsecutiveFailures + 1;
            var alreadyAlerted = current.HasAlerted;
            _state[task.Id] = new TaskState
            {
                ConsecutiveFailures = newStreak,
                HasAlerted = alreadyAlerted || newStreak >= Threshold,
            };

            // 達門檻當下那一次才送 — 後續同連續期間不重送
            if (newStreak == Threshold)
            {
                // 前綴 streak 標記到 ErrorMessage，讓接收端清楚這是 escalation 不是 first-fail
                run.ErrorMessage = $"[STREAK {newStreak}/{Threshold}] {run.ErrorMessage ?? ""}";
                await _inner.NotifyRunOutcomeAsync(task, run, ct);
            }
            // newStreak < Threshold → 沉默（避免 alert fatigue）
            // newStreak > Threshold → 沉默（已經 alert 過，等 recovery 才再開口）
        }
        else if (run.Status == RunStatus.Success)
        {
            if (current.HasAlerted && RecoveryNotificationsEnabled)
            {
                run.ErrorMessage = $"[RECOVERY after {current.ConsecutiveFailures} consecutive failures]";
                await _inner.NotifyRunOutcomeAsync(task, run, ct);
            }
            _state[task.Id] = default; // reset
        }
    }

    public readonly record struct TaskState
    {
        public int ConsecutiveFailures { get; init; }
        public bool HasAlerted { get; init; }
    }
}
