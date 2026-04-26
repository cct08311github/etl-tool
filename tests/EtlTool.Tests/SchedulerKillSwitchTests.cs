using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;

namespace EtlTool.Tests;

public class SchedulerKillSwitchTests
{
    private sealed class CapturingAudit : IAuditLogger
    {
        public List<(AuditCategory cat, AuditAction act, string msg, AuditSeverity sev, string? actor)> Calls { get; } = new();

        public Task LogAsync(AuditCategory category, AuditAction action, string message,
            string? targetType = null, Guid? targetId = null, string? targetName = null,
            AuditSeverity severity = AuditSeverity.Info, string? detailsJson = null, string? actor = null,
            CancellationToken ct = default)
        {
            Calls.Add((category, action, message, severity, actor));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Initial_state_is_not_paused()
    {
        var ks = new SchedulerKillSwitch();
        Assert.False(ks.IsPaused);
        Assert.Null(ks.PausedBy);
        Assert.Null(ks.PausedAtUtc);
    }

    [Fact]
    public async Task Pause_sets_state_and_audits()
    {
        var audit = new CapturingAudit();
        var ks = new SchedulerKillSwitch(audit);
        await ks.PauseAsync("alice", "DB issue");

        Assert.True(ks.IsPaused);
        Assert.Equal("alice", ks.PausedBy);
        Assert.Equal("DB issue", ks.PauseReason);
        Assert.NotNull(ks.PausedAtUtc);
        Assert.Single(audit.Calls);
        Assert.Equal(AuditSeverity.Warning, audit.Calls[0].sev);
        Assert.Contains("暫停", audit.Calls[0].msg);
        Assert.Equal("alice", audit.Calls[0].actor);
    }

    [Fact]
    public async Task Resume_clears_state_and_audits()
    {
        var audit = new CapturingAudit();
        var ks = new SchedulerKillSwitch(audit);
        await ks.PauseAsync("alice", "x");
        await ks.ResumeAsync("bob");

        Assert.False(ks.IsPaused);
        Assert.Null(ks.PausedBy);
        Assert.Null(ks.PausedAtUtc);
        Assert.Equal(2, audit.Calls.Count);
        Assert.Contains("恢復", audit.Calls[1].msg);
        Assert.Equal("bob", audit.Calls[1].actor);
    }

    [Fact]
    public async Task Pause_when_already_paused_is_idempotent()
    {
        var audit = new CapturingAudit();
        var ks = new SchedulerKillSwitch(audit);
        await ks.PauseAsync("alice", "first");
        await ks.PauseAsync("bob",   "second");  // ignored
        Assert.Equal("alice", ks.PausedBy);
        Assert.Equal("first", ks.PauseReason);
        Assert.Single(audit.Calls);   // only the first pause audited
    }

    [Fact]
    public async Task Resume_when_not_paused_is_noop()
    {
        var audit = new CapturingAudit();
        var ks = new SchedulerKillSwitch(audit);
        await ks.ResumeAsync("alice");
        Assert.False(ks.IsPaused);
        Assert.Empty(audit.Calls);
    }
}

public class EtlEngineMaskValueTests
{
    [Theory]
    [InlineData("Alice",    "A***e")]
    [InlineData("Anderson", "A******n")]
    [InlineData("0912345678", "0********8")]
    [InlineData("AB",       "AB")]              // ≤ 4 不遮罩
    [InlineData("ABCD",     "ABCD")]            // ≤ 4 不遮罩
    [InlineData("ABCDE",    "A***E")]           // 5 字 → mask
    public void Mask_strings_above_threshold(string input, string expected)
    {
        Assert.Equal(expected, EtlEngine.MaskValue(input));
    }

    [Fact]
    public void Mask_null_returns_null()
    {
        Assert.Null(EtlEngine.MaskValue(null));
    }

    [Theory]
    [InlineData(123)]
    [InlineData(123L)]
    [InlineData(123.45)]
    [InlineData(true)]
    public void Mask_numeric_and_bool_unchanged(object value)
    {
        Assert.Equal(value, EtlEngine.MaskValue(value));
    }

    [Fact]
    public void Mask_datetime_unchanged()
    {
        var dt = new DateTime(2026, 4, 26);
        Assert.Equal(dt, EtlEngine.MaskValue(dt));
    }
}
