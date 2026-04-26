using EtlTool.App.Services;

namespace EtlTool.Tests;

public class ApprovalExpirySweepScheduleTests
{
    [Fact]
    public void Before_run_time_today_returns_today_03_15()
    {
        var now = new DateTime(2026, 4, 26, 2, 0, 0, DateTimeKind.Local);
        var next = ApprovalExpirySweepService.NextLocalRun(now, hourLocal: 3, minuteLocal: 15);
        Assert.Equal(new DateTime(2026, 4, 26, 3, 15, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Exactly_at_run_time_returns_tomorrow()
    {
        var now = new DateTime(2026, 4, 26, 3, 15, 0, DateTimeKind.Local);
        var next = ApprovalExpirySweepService.NextLocalRun(now, hourLocal: 3, minuteLocal: 15);
        Assert.Equal(new DateTime(2026, 4, 27, 3, 15, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Just_after_03_00_but_before_03_15_returns_today_03_15()
    {
        // 03:10 < 03:15 — should still run today
        var now = new DateTime(2026, 4, 26, 3, 10, 0, DateTimeKind.Local);
        var next = ApprovalExpirySweepService.NextLocalRun(now, hourLocal: 3, minuteLocal: 15);
        Assert.Equal(new DateTime(2026, 4, 26, 3, 15, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void After_run_time_returns_tomorrow()
    {
        var now = new DateTime(2026, 4, 26, 14, 30, 0, DateTimeKind.Local);
        var next = ApprovalExpirySweepService.NextLocalRun(now, hourLocal: 3, minuteLocal: 15);
        Assert.Equal(new DateTime(2026, 4, 27, 3, 15, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Crosses_month_boundary()
    {
        var now = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Local);
        var next = ApprovalExpirySweepService.NextLocalRun(now, hourLocal: 3, minuteLocal: 15);
        Assert.Equal(new DateTime(2026, 5, 1, 3, 15, 0, DateTimeKind.Local), next);
    }
}
