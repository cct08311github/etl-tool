using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

/// <summary>
/// 驗 FileSourceScanner 的 glob 比對 + FIFO 排序 + MaxFilesPerRun 限制 + ApplyPostAction
/// 的 archive / delete / none 行為。
/// </summary>
public class FileSourceScannerTests : IDisposable
{
    private readonly string _tempDir;

    public FileSourceScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "etltool-scan-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string Touch(string name, DateTime mtimeUtc)
    {
        var p = Path.Combine(_tempDir, name);
        File.WriteAllText(p, "dummy");
        File.SetLastWriteTimeUtc(p, mtimeUtc);
        return p;
    }

    [Fact]
    public void Empty_dir_returns_empty()
    {
        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "*.csv",
        });
        Assert.Empty(matches);
    }

    [Fact]
    public void Glob_matches_extension()
    {
        Touch("orders.csv", DateTime.UtcNow);
        Touch("notes.txt", DateTime.UtcNow);
        Touch("bad.json", DateTime.UtcNow);

        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "*.csv",
            MaxFilesPerRun = 0,
        });
        Assert.Single(matches);
        Assert.Equal("orders.csv", matches[0].FileName);
    }

    [Fact]
    public void Returns_oldest_first_fifo()
    {
        var newer = Touch("c.csv", DateTime.UtcNow);
        var middle = Touch("b.csv", DateTime.UtcNow.AddMinutes(-5));
        var oldest = Touch("a.csv", DateTime.UtcNow.AddMinutes(-10));

        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "*.csv",
            MaxFilesPerRun = 0,  // 不限制
        });
        Assert.Equal(3, matches.Count);
        Assert.Equal("a.csv", matches[0].FileName);
        Assert.Equal("b.csv", matches[1].FileName);
        Assert.Equal("c.csv", matches[2].FileName);
    }

    [Fact]
    public void MaxFilesPerRun_caps_results()
    {
        Touch("a.csv", DateTime.UtcNow.AddMinutes(-30));
        Touch("b.csv", DateTime.UtcNow.AddMinutes(-20));
        Touch("c.csv", DateTime.UtcNow.AddMinutes(-10));

        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "*.csv",
            MaxFilesPerRun = 2,
        });
        Assert.Equal(2, matches.Count);
        Assert.Equal("a.csv", matches[0].FileName);
        Assert.Equal("b.csv", matches[1].FileName);
    }

    [Fact]
    public void Throws_for_missing_directory()
    {
        Assert.Throws<DirectoryNotFoundException>(() => FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = Path.Combine(_tempDir, "does-not-exist"),
            GlobPattern = "*.csv",
        }));
    }

    [Fact]
    public void Throws_for_empty_directory_path()
    {
        Assert.Throws<InvalidOperationException>(() => FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = "",
        }));
    }

    [Fact]
    public void Excludes_files_already_in_archive_subdir()
    {
        var archive = Path.Combine(_tempDir, "archive");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "old.csv"), "x");
        Touch("new.csv", DateTime.UtcNow);

        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "*.csv",
            ArchiveDirectory = "archive",
            MaxFilesPerRun = 0,
        });
        Assert.Single(matches);
        Assert.Equal("new.csv", matches[0].FileName);
    }

    [Fact]
    public void ApplyPostAction_archive_moves_with_timestamp_suffix()
    {
        var path = Touch("daily.csv", DateTime.UtcNow);
        var msg = FileSourceScanner.ApplyPostAction(path, new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            ArchiveDirectory = "archive",
            PostAction = FilePostAction.Archive,
        });
        Assert.False(File.Exists(path));
        var archive = Path.Combine(_tempDir, "archive");
        Assert.True(Directory.Exists(archive));
        var moved = Directory.GetFiles(archive);
        Assert.Single(moved);
        Assert.Matches(@"daily_\d{14}\.csv$", moved[0]);
        Assert.Contains("已歸檔", msg);
    }

    [Fact]
    public void ApplyPostAction_delete_removes_file()
    {
        var path = Touch("oneoff.csv", DateTime.UtcNow);
        FileSourceScanner.ApplyPostAction(path, new FileSourceConfig { PostAction = FilePostAction.Delete });
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ApplyPostAction_none_keeps_file()
    {
        var path = Touch("keep.csv", DateTime.UtcNow);
        FileSourceScanner.ApplyPostAction(path, new FileSourceConfig { PostAction = FilePostAction.None });
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Glob_pattern_with_date_token_resolves_at_scan_time()
    {
        // 模擬 referenceNow = 2026-04-27 → ${TODAY:yyyyMMdd} → 20260427
        Touch("orders_20260427.csv", DateTime.UtcNow);
        Touch("orders_20260426.csv", DateTime.UtcNow.AddDays(-1));

        var refNow = new DateTime(2026, 4, 27, 10, 0, 0);
        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "orders_${TODAY:yyyyMMdd}.csv",
            MaxFilesPerRun = 0,
        }, refNow);
        Assert.Single(matches);
        Assert.Equal("orders_20260427.csv", matches[0].FileName);
    }

    [Fact]
    public void DirectoryPath_with_date_token_resolves()
    {
        var refNow = new DateTime(2026, 4, 27);
        var dayDir = Path.Combine(_tempDir, "2026-04-27");
        Directory.CreateDirectory(dayDir);
        File.WriteAllText(Path.Combine(dayDir, "today.csv"), "x");

        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = Path.Combine(_tempDir, "${TODAY:yyyy-MM-dd}"),
            GlobPattern = "*.csv",
            MaxFilesPerRun = 0,
        }, refNow);
        Assert.Single(matches);
        Assert.Equal("today.csv", matches[0].FileName);
    }

    [Fact]
    public void Yesterday_token_resolves_correctly()
    {
        Touch("daily_20260426.csv", DateTime.UtcNow);
        var refNow = new DateTime(2026, 4, 27);
        var matches = FileSourceScanner.Scan(new FileSourceConfig
        {
            DirectoryPath = _tempDir,
            GlobPattern = "daily_${YESTERDAY:yyyyMMdd}.csv",
            MaxFilesPerRun = 0,
        }, refNow);
        Assert.Single(matches);
    }
}
