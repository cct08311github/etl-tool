using System.Runtime.InteropServices;
using EtlTool.App.Services;

namespace EtlTool.Tests;

public class DataDirPermissionCheckTests
{
    [Fact]
    public void Nonexistent_dir_returns_skipped()
    {
        var path = Path.Combine(Path.GetTempPath(), "etltool-test-nonexistent-" + Guid.NewGuid());
        var result = DataDirPermissionCheck.Inspect(path);
        Assert.Equal(DataDirPermissionLevel.Skipped, result.Level);
        Assert.Contains("不存在", result.Detail);
    }

    [Fact]
    public void Windows_returns_skipped_with_icacls_hint()
    {
        // On Windows: returns Skipped with icacls hint. On Linux/macOS: this assertion
        // doesn't apply (we test the POSIX path separately). Skip rather than fail.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Directory.CreateTempSubdirectory("etltool-test-").FullName;
        try
        {
            var result = DataDirPermissionCheck.Inspect(dir);
            Assert.Equal(DataDirPermissionLevel.Skipped, result.Level);
            Assert.Contains("icacls", result.Detail);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Posix_700_dir_with_600_files_returns_Ok()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Directory.CreateTempSubdirectory("etltool-test-").FullName;
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var f = Path.Combine(dir, "key1.xml");
            File.WriteAllText(f, "test");
            File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var result = DataDirPermissionCheck.Inspect(dir);
            Assert.True(result.Level == DataDirPermissionLevel.Ok,
                $"expected Ok, got {result.Level}: {result.Detail}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Posix_world_readable_dir_returns_Warn()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Directory.CreateTempSubdirectory("etltool-test-").FullName;
        try
        {
            // 755 = user rwx + group r-x + other r-x → other read 命中
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var result = DataDirPermissionCheck.Inspect(dir);
            Assert.Equal(DataDirPermissionLevel.Warn, result.Level);
            Assert.Contains("other read", result.Detail);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Posix_world_readable_file_returns_Warn()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Directory.CreateTempSubdirectory("etltool-test-").FullName;
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var f = Path.Combine(dir, "key1.xml");
            File.WriteAllText(f, "test");
            // 644 = user rw + group r + other r
            File.SetUnixFileMode(f,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            var result = DataDirPermissionCheck.Inspect(dir);
            Assert.Equal(DataDirPermissionLevel.Warn, result.Level);
            Assert.Contains("key1.xml", result.Detail);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Posix_group_writable_dir_returns_Warn()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var dir = Directory.CreateTempSubdirectory("etltool-test-").FullName;
        try
        {
            // 770 = user rwx + group rwx + nothing other → group write 命中
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

            var result = DataDirPermissionCheck.Inspect(dir);
            Assert.Equal(DataDirPermissionLevel.Warn, result.Level);
            Assert.Contains("group write", result.Detail);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
