using System.Runtime.InteropServices;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Services;

/// <summary>
/// 啟動時檢查 dataDir/keys 資料夾權限是否過於寬鬆 — 連線字串解密金鑰落在這裡，
/// 若被 world-readable 任何同機帳號可讀就能解密所有連線字串。
///
/// POSIX (Linux / macOS)：
///   - 期望 keys 目錄 mode = 700（owner only）
///   - 期望檔案 mode = 600
///   - 任何 group/other read 旗標 → audit Warning
///
/// Windows：
///   - 沒有 mode 概念，但檢查 ACL 是否含 Everyone / Authenticated Users 的 read。
///   - 為保守起見，目前 Windows 路徑只 log Info「未做 ACL 自動檢查」並建議手動以
///     icacls 確認。後續可以擴充。
/// </summary>
public static class DataDirPermissionCheck
{
    public sealed record CheckResult(
        DataDirPermissionLevel Level,
        string Detail);

    /// <summary>
    /// 檢查 keys 目錄與其下檔案。回傳結果（不會 throw）。
    /// </summary>
    public static CheckResult Inspect(string keysDir)
    {
        if (!Directory.Exists(keysDir))
            return new CheckResult(DataDirPermissionLevel.Skipped, $"目錄不存在：{keysDir}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new CheckResult(DataDirPermissionLevel.Skipped,
                $"Windows 跳過 mode 檢查；請以 icacls \"{keysDir}\" 確認僅 service 帳戶可讀");
        }

        // POSIX: 透過 UnixFileMode（.NET 7+）讀
        try
        {
            var dirMode = File.GetUnixFileMode(keysDir);
            var dirIssues = DescribeOverlyOpenMode(dirMode, isDirectory: true);

            var fileIssues = new List<string>();
            foreach (var f in Directory.EnumerateFiles(keysDir))
            {
                try
                {
                    var fm = File.GetUnixFileMode(f);
                    var issue = DescribeOverlyOpenMode(fm, isDirectory: false);
                    if (issue is not null)
                        fileIssues.Add($"{Path.GetFileName(f)}: {issue}");
                }
                catch { /* 個別檔案讀不到 mode 就略過 */ }
            }

            if (dirIssues is null && fileIssues.Count == 0)
                return new CheckResult(DataDirPermissionLevel.Ok,
                    $"權限正確（keys 目錄 mode = {Convert.ToString((int)dirMode, 8)}；所有檔案 mode 含 group/other read = false）");

            var detail = $"keys 目錄 mode 過於寬鬆：" +
                         (dirIssues is null ? "(目錄 OK) " : $"目錄 - {dirIssues}; ") +
                         (fileIssues.Count == 0 ? "" : "檔案 - " + string.Join("; ", fileIssues));
            return new CheckResult(DataDirPermissionLevel.Warn, detail);
        }
        catch (Exception ex)
        {
            return new CheckResult(DataDirPermissionLevel.Skipped,
                $"讀取 mode 失敗（{ex.GetType().Name}）— 無法判定，請手動確認");
        }
    }

    /// <summary>
    /// 若 mode 含 group/other read 任何一個 → 回傳描述字串；OK 時回 null。
    /// </summary>
    private static string? DescribeOverlyOpenMode(UnixFileMode mode, bool isDirectory)
    {
        var bad = new List<string>();
        if ((mode & UnixFileMode.GroupRead) != 0) bad.Add("group read");
        if ((mode & UnixFileMode.GroupWrite) != 0) bad.Add("group write");
        if ((mode & UnixFileMode.OtherRead) != 0) bad.Add("other read");
        if ((mode & UnixFileMode.OtherWrite) != 0) bad.Add("other write");
        if (bad.Count == 0) return null;
        var expected = isDirectory ? "700" : "600";
        return $"含 {string.Join(", ", bad)}（預期 mode = {expected}，實際 = {Convert.ToString((int)mode, 8)}）";
    }

    /// <summary>啟動 hook：檢查 + 寫 audit。</summary>
    public static async Task RunAndAuditAsync(
        string keysDir, IAuditLogger audit, ILogger log, CancellationToken ct)
    {
        var result = Inspect(keysDir);
        var severity = result.Level switch
        {
            DataDirPermissionLevel.Warn => AuditSeverity.Warning,
            DataDirPermissionLevel.Ok => AuditSeverity.Info,
            _ => AuditSeverity.Info,
        };
        var prefix = result.Level switch
        {
            DataDirPermissionLevel.Warn => "⚠ Data Protection 金鑰目錄權限過於寬鬆",
            DataDirPermissionLevel.Ok => "✓ Data Protection 金鑰目錄權限檢查",
            _ => "ℹ Data Protection 金鑰目錄權限檢查",
        };

        if (result.Level == DataDirPermissionLevel.Warn)
            log.LogWarning("[SECURITY] {Prefix}: {Detail}", prefix, result.Detail);
        else
            log.LogInformation("{Prefix}: {Detail}", prefix, result.Detail);

        await audit.LogAsync(AuditCategory.System, AuditAction.SystemStart,
            $"{prefix}: {result.Detail}",
            severity: severity, actor: "system", ct: ct);
    }
}

public enum DataDirPermissionLevel
{
    Ok = 0,
    Warn = 1,
    Skipped = 2,
}
