using EtlTool.Core.Scheduling;

namespace EtlTool.Tests;

public class MaintenanceWindowTests
{
    [Theory]
    // Sun = 4/26/2026 (Sunday)
    [InlineData("All", "02:00", "04:00", "2026-04-26 02:30:00", true,  "all-days, in window")]
    [InlineData("All", "02:00", "04:00", "2026-04-26 04:00:00", false, "all-days, exact end (exclusive)")]
    [InlineData("All", "02:00", "04:00", "2026-04-26 01:59:59", false, "all-days, before window")]
    [InlineData("Sat,Sun", "00:00", "23:59", "2026-04-26 14:00:00", true, "weekend match Sun")]
    [InlineData("Sat,Sun", "00:00", "23:59", "2026-04-27 14:00:00", false, "weekend not Mon")]
    [InlineData("Mon", "09:00", "17:00", "2026-04-27 12:00:00", true, "Mon work hours")]
    [InlineData("Mon", "09:00", "17:00", "2026-04-26 12:00:00", false, "Mon spec but Sun")]
    public void IsActive_named_days(string daysCsv, string from, string to, string nowIso, bool expected, string label)
    {
        var w = new MaintenanceWindow
        {
            Days = daysCsv.Split(','),
            From = from,
            To = to,
        };
        var now = DateTime.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, w.IsActive(now));
    }

    [Theory]
    [InlineData("22:00", "02:00", "2026-04-26 23:00:00", true,  "after 22:00 same day")]
    [InlineData("22:00", "02:00", "2026-04-27 01:30:00", true,  "before 02:00 next day")]
    [InlineData("22:00", "02:00", "2026-04-27 02:00:00", false, "exactly 02:00 (exclusive)")]
    [InlineData("22:00", "02:00", "2026-04-26 21:59:59", false, "before 22:00")]
    [InlineData("22:00", "02:00", "2026-04-26 12:00:00", false, "noon")]
    public void IsActive_overnight_window(string from, string to, string nowIso, bool expected, string _)
    {
        var w = new MaintenanceWindow { Days = new[] { "All" }, From = from, To = to };
        var now = DateTime.Parse(nowIso, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, w.IsActive(now));
    }

    [Fact]
    public void Empty_days_never_active()
    {
        var w = new MaintenanceWindow { Days = Array.Empty<string>(), From = "00:00", To = "23:59" };
        Assert.False(w.IsActive(DateTime.Now));
    }

    [Fact]
    public void Invalid_time_string_never_active()
    {
        var w = new MaintenanceWindow { Days = new[] { "All" }, From = "garbage", To = "04:00" };
        Assert.False(w.IsActive(new DateTime(2026, 4, 26, 3, 0, 0)));
    }

    [Fact]
    public void Options_returns_first_matching_window()
    {
        var opts = new MaintenanceWindowsOptions
        {
            Windows =
            {
                new MaintenanceWindow { Days = new[] { "Mon" }, From = "09:00", To = "10:00", Reason = "A" },
                new MaintenanceWindow { Days = new[] { "All" }, From = "02:00", To = "04:00", Reason = "B" },
            },
        };
        // Sunday 03:00 → only second window matches
        var hit = opts.CurrentlyActive(new DateTime(2026, 4, 26, 3, 0, 0));
        Assert.NotNull(hit);
        Assert.Equal("B", hit!.Reason);

        // Monday 09:30 → first window matches first
        var hit2 = opts.CurrentlyActive(new DateTime(2026, 4, 27, 9, 30, 0));
        Assert.NotNull(hit2);
        Assert.Equal("A", hit2!.Reason);

        // Tuesday noon → none
        Assert.Null(opts.CurrentlyActive(new DateTime(2026, 4, 28, 12, 0, 0)));
    }
}
