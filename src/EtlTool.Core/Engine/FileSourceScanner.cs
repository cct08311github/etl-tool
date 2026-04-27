using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 掃描 <see cref="FileSourceConfig.DirectoryPath"/> 下符合 glob 的檔案，依 mtime
/// 由舊到新排序回傳。給 EtlEngine File 模式單次執行用 — 一次處理 1～N 個檔。
///
/// 純函式設計（除了 IO），所有 path 處理都用 Path.* + DirectoryInfo，跨平台。
/// 不持久化「已處理過的檔案」清單 — 防重複的責任在 PostAction（archive/delete）
/// 或檔名規則（含時間戳）。
/// </summary>
public static class FileSourceScanner
{
    public sealed record FileMatch(string FullPath, string FileName, long SizeBytes, DateTime LastWriteUtc);

    public static List<FileMatch> Scan(FileSourceConfig config) => Scan(config, DateTime.Now);

    public static List<FileMatch> Scan(FileSourceConfig config, DateTime referenceNow)
    {
        if (string.IsNullOrWhiteSpace(config.DirectoryPath))
            throw new InvalidOperationException("FileSourceConfig.DirectoryPath 未設定。");

        // 把日期 token 替換掉再做 glob match
        // 範例：/data/inbox/${TODAY:yyyy-MM-dd}/  → /data/inbox/2026-04-27/
        //      orders_${YESTERDAY:yyyyMMdd}_*.csv → orders_20260426_*.csv
        var resolvedDir = DateTokenResolver.SubstituteFilePath(config.DirectoryPath, referenceNow);
        var resolvedGlob = string.IsNullOrWhiteSpace(config.GlobPattern)
            ? "*"
            : DateTokenResolver.SubstituteFilePath(config.GlobPattern.Trim(), referenceNow);

        var dir = new DirectoryInfo(resolvedDir);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"來源目錄不存在：{resolvedDir}");

        var pattern = resolvedGlob;
        var matches = dir.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            // 排除 archive 目錄裡的檔案（如果使用者把 archive 設在同一個目錄底下）
            .Where(f => !IsInArchiveSubdir(f.FullName, dir.FullName, config.ArchiveDirectory))
            .OrderBy(f => f.LastWriteTimeUtc)  // FIFO — 最舊優先
            .Select(f => new FileMatch(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();

        if (config.MaxFilesPerRun > 0 && matches.Count > config.MaxFilesPerRun)
            matches = matches.Take(config.MaxFilesPerRun).ToList();

        return matches;
    }

    private static bool IsInArchiveSubdir(string filePath, string scanRoot, string? archiveDir)
    {
        if (string.IsNullOrEmpty(archiveDir)) return false;
        try
        {
            // 把 archiveDir 解析為絕對路徑（相對路徑 → scanRoot 為基準）
            var resolved = Path.IsPathRooted(archiveDir)
                ? Path.GetFullPath(archiveDir)
                : Path.GetFullPath(Path.Combine(scanRoot, archiveDir));
            return filePath.StartsWith(resolved, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 處理完一個檔之後，依 PostAction 動作。回傳實際採取的描述（給 audit log）。
    /// 失敗（i/o error）會 throw — 呼叫端應該包進 try/catch 並記入 RunHistory。
    /// </summary>
    public static string ApplyPostAction(string filePath, FileSourceConfig config)
    {
        switch (config.PostAction)
        {
            case FilePostAction.None:
                return $"檔案 {Path.GetFileName(filePath)} 留在原處";

            case FilePostAction.Delete:
                File.Delete(filePath);
                return $"已刪除檔案 {Path.GetFileName(filePath)}";

            case FilePostAction.Archive:
            default:
                var srcDir = Path.GetDirectoryName(filePath) ?? config.DirectoryPath;
                var archive = string.IsNullOrEmpty(config.ArchiveDirectory)
                    ? Path.Combine(srcDir, "archive")
                    : (Path.IsPathRooted(config.ArchiveDirectory)
                        ? config.ArchiveDirectory
                        : Path.Combine(srcDir, config.ArchiveDirectory));
                Directory.CreateDirectory(archive);
                var name = Path.GetFileNameWithoutExtension(filePath);
                var ext = Path.GetExtension(filePath);
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var dest = Path.Combine(archive, $"{name}_{stamp}{ext}");
                // 若同檔名碰撞（同秒處理多檔）→ 加序號
                int n = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(archive, $"{name}_{stamp}_{n}{ext}");
                    n++;
                }
                File.Move(filePath, dest);
                return $"已歸檔檔案 {Path.GetFileName(filePath)} → {dest}";
        }
    }
}
