using System.Collections.Concurrent;

namespace EtlTool.App.Auth;

/// <summary>
/// In-memory login attempt tracker / lockout 機制。Singleton。
///
/// 規則（可由 AuthOptions 微調）：
///   - 連續 N 次失敗（預設 5）→ 鎖 M 分鐘（預設 15）
///   - 鎖定期間繼續輸入 → 計數歸零後重新計
///   - 成功登入 → 清空該帳號計數
///   - 觀察視窗：W 分鐘內（預設 10）累計 N 次才算 lockout
///
/// 用 in-memory 而非 DB：
///   - 重啟即清空（可接受；正式應該配 distributed lock，目前單機足夠）
///   - 不增加 DB 負擔，每次登入嘗試 O(1)
///   - 多 instance 部署時不防共享，但本系統目前是 single-host
/// </summary>
public sealed class LoginLockoutService
{
    private sealed class State
    {
        public int FailCount;
        public DateTime FirstFailUtc;
        public DateTime? LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, State> _byUser = new(StringComparer.Ordinal);
    private readonly LoginLockoutOptions _options;

    public LoginLockoutService(LoginLockoutOptions options)
    {
        _options = options;
    }

    /// <summary>檢查目前是否被鎖；回傳剩餘鎖定秒數（>0 = 鎖中）。</summary>
    public int GetLockedSeconds(string username)
    {
        if (!_byUser.TryGetValue(username, out var state)) return 0;
        if (state.LockedUntilUtc is null) return 0;
        var remaining = (state.LockedUntilUtc.Value - DateTime.UtcNow).TotalSeconds;
        if (remaining <= 0)
        {
            // 鎖期過了 — 清空
            _byUser.TryRemove(username, out _);
            return 0;
        }
        return (int)Math.Ceiling(remaining);
    }

    /// <summary>記錄一次失敗。回傳：(目前失敗數, 是否剛達鎖定門檻)。</summary>
    public (int FailCount, bool JustLocked) RecordFailure(string username)
    {
        var now = DateTime.UtcNow;
        bool justLocked = false;

        var state = _byUser.AddOrUpdate(username,
            _ => new State { FailCount = 1, FirstFailUtc = now },
            (_, existing) =>
            {
                // 觀察視窗外：歸零重新計
                if ((now - existing.FirstFailUtc).TotalMinutes > _options.WindowMinutes)
                {
                    existing.FailCount = 1;
                    existing.FirstFailUtc = now;
                    existing.LockedUntilUtc = null;
                    return existing;
                }
                existing.FailCount++;
                if (existing.FailCount >= _options.MaxFailures && existing.LockedUntilUtc is null)
                {
                    existing.LockedUntilUtc = now.AddMinutes(_options.LockoutMinutes);
                    justLocked = true;
                }
                return existing;
            });

        return (state.FailCount, justLocked);
    }

    /// <summary>成功登入：清空該帳號計數。</summary>
    public void RecordSuccess(string username)
    {
        _byUser.TryRemove(username, out _);
    }
}

public sealed class LoginLockoutOptions
{
    public int MaxFailures { get; set; } = 5;
    public int WindowMinutes { get; set; } = 10;
    public int LockoutMinutes { get; set; } = 15;
}
