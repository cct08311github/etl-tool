using EtlTool.App.Services;

namespace EtlTool.Tests;

public class NightlyBackupScheduleTests
{
    [Fact]
    public void Before_run_time_today_returns_today_03_45()
    {
        var now = new DateTime(2026, 4, 27, 1, 0, 0, DateTimeKind.Local);
        var next = NightlyBackupService.NextLocalRun(now, hourLocal: 3, minuteLocal: 45);
        Assert.Equal(new DateTime(2026, 4, 27, 3, 45, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Exactly_at_run_time_returns_tomorrow()
    {
        var now = new DateTime(2026, 4, 27, 3, 45, 0, DateTimeKind.Local);
        var next = NightlyBackupService.NextLocalRun(now, hourLocal: 3, minuteLocal: 45);
        Assert.Equal(new DateTime(2026, 4, 28, 3, 45, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void After_run_time_returns_tomorrow()
    {
        var now = new DateTime(2026, 4, 27, 14, 0, 0, DateTimeKind.Local);
        var next = NightlyBackupService.NextLocalRun(now, hourLocal: 3, minuteLocal: 45);
        Assert.Equal(new DateTime(2026, 4, 28, 3, 45, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Crosses_month_boundary()
    {
        var now = new DateTime(2026, 4, 30, 23, 0, 0, DateTimeKind.Local);
        var next = NightlyBackupService.NextLocalRun(now, hourLocal: 3, minuteLocal: 45);
        Assert.Equal(new DateTime(2026, 5, 1, 3, 45, 0, DateTimeKind.Local), next);
    }

    [Fact]
    public void Prune_keeps_newest_n_files()
    {
        var dir = Directory.CreateTempSubdirectory("etltool-bk-").FullName;
        try
        {
            // 建 10 個檔案 etltool-2026MMDD-...db
            for (int i = 1; i <= 10; i++)
            {
                var path = Path.Combine(dir, $"etltool-2026{i:D2}01-000000.db");
                File.WriteAllText(path, "x");
            }

            var deleted = NightlyBackupService.PruneOldBackups(dir, retainCount: 3);
            Assert.Equal(7, deleted);

            var remaining = Directory.GetFiles(dir, "etltool-*.db")
                .Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(3, remaining.Count);
            // 字典序最新 3 個 = 08, 09, 10
            Assert.Contains(remaining, n => n!.Contains("202608"));
            Assert.Contains(remaining, n => n!.Contains("202609"));
            Assert.Contains(remaining, n => n!.Contains("202610"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Prune_with_retain_zero_or_negative_does_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("etltool-bk-").FullName;
        try
        {
            for (int i = 1; i <= 5; i++)
                File.WriteAllText(Path.Combine(dir, $"etltool-2026{i:D2}01-000000.db"), "x");

            Assert.Equal(0, NightlyBackupService.PruneOldBackups(dir, 0));
            Assert.Equal(0, NightlyBackupService.PruneOldBackups(dir, -1));
            Assert.Equal(5, Directory.GetFiles(dir, "etltool-*.db").Length);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Prune_nonexistent_dir_returns_zero()
    {
        var fakeDir = Path.Combine(Path.GetTempPath(), "etltool-nonexistent-" + Guid.NewGuid());
        Assert.Equal(0, NightlyBackupService.PruneOldBackups(fakeDir, 5));
    }

    [Fact]
    public void Prune_only_targets_etltool_db_files()
    {
        var dir = Directory.CreateTempSubdirectory("etltool-bk-").FullName;
        try
        {
            // 故意混入無關檔案
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "other.db"), "x");
            for (int i = 1; i <= 5; i++)
                File.WriteAllText(Path.Combine(dir, $"etltool-2026{i:D2}01-000000.db"), "x");

            NightlyBackupService.PruneOldBackups(dir, retainCount: 1);

            // 無關檔案不應該被刪
            Assert.True(File.Exists(Path.Combine(dir, "readme.txt")));
            Assert.True(File.Exists(Path.Combine(dir, "other.db")));
            // etltool-*.db 只剩 1 個
            Assert.Single(Directory.GetFiles(dir, "etltool-*.db"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
