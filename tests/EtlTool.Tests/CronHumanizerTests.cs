using EtlTool.Core.Engine;

namespace EtlTool.Tests;

public class CronHumanizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not a cron")]      // 不夠欄位
    [InlineData("0 0")]              // 不夠欄位
    public void Returns_null_for_invalid_input(string? cron)
    {
        Assert.Null(CronHumanizer.Humanize(cron));
    }

    [Theory]
    [InlineData("0 * * * * ?", "每分鐘")]
    [InlineData("0 0/5 * * * ?", "每 5 分鐘")]
    [InlineData("0 0/10 * * * ?", "每 10 分鐘")]
    [InlineData("0 0/30 * * * ?", "每 30 分鐘")]
    [InlineData("0 */15 * * * ?", "每 15 分鐘")]
    public void Every_n_minutes_pattern(string cron, string expected)
    {
        Assert.Equal(expected, CronHumanizer.Humanize(cron));
    }

    [Fact]
    public void Every_hour_pattern()
    {
        Assert.Equal("每小時整點", CronHumanizer.Humanize("0 0 * * * ?"));
    }

    [Theory]
    [InlineData("0 0 0/2 * * ?", "每 2 小時整點")]
    [InlineData("0 0 0/4 * * ?", "每 4 小時整點")]
    [InlineData("0 0 */6 * * ?", "每 6 小時整點")]
    public void Every_n_hours_pattern(string cron, string expected)
    {
        Assert.Equal(expected, CronHumanizer.Humanize(cron));
    }

    [Theory]
    [InlineData("0 0 2 * * ?", "每天 02:00")]
    [InlineData("0 30 6 * * ?", "每天 06:30")]
    [InlineData("0 0 22 * * ?", "每天 22:00")]
    public void Daily_at_fixed_time_pattern(string cron, string expected)
    {
        Assert.Equal(expected, CronHumanizer.Humanize(cron));
    }

    [Theory]
    [InlineData("0 0 3 ? * MON", "每週一 03:00")]
    [InlineData("0 0 3 ? * SUN", "每週日 03:00")]
    [InlineData("0 30 8 ? * FRI", "每週五 08:30")]
    public void Weekly_at_fixed_time_pattern(string cron, string expected)
    {
        Assert.Equal(expected, CronHumanizer.Humanize(cron));
    }

    [Theory]
    [InlineData("0 0 2 1 * ?", "每月 1 號 02:00")]
    [InlineData("0 0 12 15 * ?", "每月 15 號 12:00")]
    public void Monthly_at_fixed_day_and_time(string cron, string expected)
    {
        Assert.Equal(expected, CronHumanizer.Humanize(cron));
    }

    [Theory]
    [InlineData("0 15 10 ? * 6L")]              // last Friday — too complex
    [InlineData("0 0 0 ? * MON-FRI")]           // weekday range — not supported
    [InlineData("0 0,30 * * * ?")]              // multiple values — not supported
    [InlineData("0 0 0 1,15 * ?")]              // multiple days of month
    public void Returns_null_for_unsupported_complex_patterns(string cron)
    {
        Assert.Null(CronHumanizer.Humanize(cron));
    }

    [Fact]
    public void Trims_whitespace_around_input()
    {
        Assert.Equal("每天 02:00", CronHumanizer.Humanize("  0 0 2 * * ?  "));
    }
}
