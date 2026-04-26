using EtlTool.App.Auth;

namespace EtlTool.Tests;

public class LoginLockoutServiceTests
{
    private static LoginLockoutService NewSvc(int max = 3, int window = 10, int lockout = 15)
        => new(new LoginLockoutOptions { MaxFailures = max, WindowMinutes = window, LockoutMinutes = lockout });

    [Fact]
    public void Fresh_user_not_locked()
    {
        Assert.Equal(0, NewSvc().GetLockedSeconds("alice"));
    }

    [Fact]
    public void RecordFailure_below_threshold_does_not_lock()
    {
        var s = NewSvc(max: 3);
        var (count1, locked1) = s.RecordFailure("alice");
        Assert.Equal(1, count1);
        Assert.False(locked1);
        var (count2, locked2) = s.RecordFailure("alice");
        Assert.Equal(2, count2);
        Assert.False(locked2);
        Assert.Equal(0, s.GetLockedSeconds("alice"));
    }

    [Fact]
    public void Threshold_failure_locks_account()
    {
        var s = NewSvc(max: 3, lockout: 15);
        s.RecordFailure("alice");
        s.RecordFailure("alice");
        var (count, justLocked) = s.RecordFailure("alice");
        Assert.Equal(3, count);
        Assert.True(justLocked);
        var sec = s.GetLockedSeconds("alice");
        Assert.InRange(sec, 14 * 60, 15 * 60);
    }

    [Fact]
    public void Subsequent_failures_after_lock_dont_relock()
    {
        var s = NewSvc(max: 3);
        for (int i = 0; i < 3; i++) s.RecordFailure("alice");

        // 已鎖；下一次 failure 不應該觸發新的 justLocked
        var (count, justLocked) = s.RecordFailure("alice");
        Assert.Equal(4, count);
        Assert.False(justLocked);
    }

    [Fact]
    public void Success_clears_failures()
    {
        var s = NewSvc(max: 3);
        s.RecordFailure("alice");
        s.RecordFailure("alice");
        s.RecordSuccess("alice");
        Assert.Equal(0, s.GetLockedSeconds("alice"));

        // 再失敗一次應該從 1 開始
        var (count, _) = s.RecordFailure("alice");
        Assert.Equal(1, count);
    }

    [Fact]
    public void Different_users_tracked_separately()
    {
        var s = NewSvc(max: 3);
        s.RecordFailure("alice");
        s.RecordFailure("alice");
        s.RecordFailure("alice");   // alice 鎖了
        var (bobCount, bobLocked) = s.RecordFailure("bob");
        Assert.Equal(1, bobCount);
        Assert.False(bobLocked);
        Assert.True(s.GetLockedSeconds("alice") > 0);
        Assert.Equal(0, s.GetLockedSeconds("bob"));
    }
}
