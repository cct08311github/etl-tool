using EtlTool.App.Services;

namespace EtlTool.Tests;

public class AuditRetentionServiceScheduleTests
{
    [Fact]
    public void Before_run_hour_today_returns_today_run()
    {
        // 02:00 < 03:00 → next run = today 03:00
        var now = new DateTime(2026, 4, 26, 2, 0, 0, DateTimeKind.Local);
        var next = AuditRetentionService.NextLocalRun(now, hourLocal: 3);
        Assert.Equal(new DateTime(2026, 4, 26, 3, 0, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Exactly_run_hour_returns_tomorrow()
    {
        // 03:00:00 === today's run → next is tomorrow
        var now = new DateTime(2026, 4, 26, 3, 0, 0, DateTimeKind.Local);
        var next = AuditRetentionService.NextLocalRun(now, hourLocal: 3);
        Assert.Equal(new DateTime(2026, 4, 27, 3, 0, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void After_run_hour_returns_tomorrow()
    {
        var now = new DateTime(2026, 4, 26, 14, 30, 0, DateTimeKind.Local);
        var next = AuditRetentionService.NextLocalRun(now, hourLocal: 3);
        Assert.Equal(new DateTime(2026, 4, 27, 3, 0, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Cross_month_boundary()
    {
        // 4/30 23:00 → next run = 5/1 03:00
        var now = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Local);
        var next = AuditRetentionService.NextLocalRun(now, hourLocal: 3);
        Assert.Equal(new DateTime(2026, 5, 1, 3, 0, 0, DateTimeKind.Local), next);
    }
}
