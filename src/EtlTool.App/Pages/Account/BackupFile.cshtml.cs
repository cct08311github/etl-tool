using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EtlTool.App.Pages.Account;

[Authorize(Roles = "Admin")]
public class BackupFileModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly IAuditLogger _audit;

    public BackupFileModel(IConfiguration config, IHostEnvironment env, IAuditLogger audit)
    {
        _config = config;
        _env = env;
        _audit = audit;
    }

    public IActionResult OnGet() => Redirect("/system");

    public async Task<IActionResult> OnGetDownloadAsync(string? name, CancellationToken ct)
    {
        if (!TryResolvePath(name, out var path, out var error))
            return Content(error!, "text/plain");

        await _audit.LogAsync(AuditCategory.System, AuditAction.Update,
            $"📥 下載備份檔 {Path.GetFileName(path)}",
            severity: AuditSeverity.Info, actor: User.Identity?.Name, ct: ct);

        var bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        return File(bytes, "application/x-sqlite3", Path.GetFileName(path));
    }

    public async Task<IActionResult> OnGetDeleteAsync(string? name, CancellationToken ct)
    {
        if (!TryResolvePath(name, out var path, out var error))
            return Content(error!, "text/plain");

        try
        {
            System.IO.File.Delete(path);
            await _audit.LogAsync(AuditCategory.System, AuditAction.Delete,
                $"🗑 刪除備份檔 {Path.GetFileName(path)}",
                severity: AuditSeverity.Warning, actor: User.Identity?.Name, ct: ct);
            return Redirect("/system");
        }
        catch (Exception ex)
        {
            await _audit.LogAsync(AuditCategory.System, AuditAction.Delete,
                $"⚠ 刪除備份檔失敗 {Path.GetFileName(path)}: {ex.Message}",
                severity: AuditSeverity.Warning, actor: User.Identity?.Name, ct: ct);
            return Content($"刪除失敗：{ex.Message}", "text/plain");
        }
    }

    /// <summary>
    /// 解析並驗證 name 確實在 backup 目錄內、檔名符合 etltool-*.db 規則。
    /// 防 path traversal（"..", absolute path 等）。
    /// </summary>
    private bool TryResolvePath(string? name, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "缺少 name 參數";
            return false;
        }

        // 不允許包含目錄分隔符
        if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            error = "檔名格式不合法";
            return false;
        }

        // 必須符合 etltool-*.db
        if (!name.StartsWith("etltool-", StringComparison.Ordinal) ||
            !name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            error = "只能存取備份檔（etltool-*.db）";
            return false;
        }

        var dataDir = _config["DataDirectory"]
                      ?? Environment.GetEnvironmentVariable("ETLTOOL_DATA_DIR")
                      ?? Path.Combine(_env.ContentRootPath, "data");
        var backupDir = _config["Backup:Directory"] ?? Path.Combine(dataDir, "backups");
        var candidate = Path.Combine(backupDir, name);

        // 雙重防 traversal：解析後的絕對路徑必須仍在 backupDir 下
        var fullCandidate = Path.GetFullPath(candidate);
        var fullBackupDir = Path.GetFullPath(backupDir);
        if (!fullCandidate.StartsWith(fullBackupDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && fullCandidate != fullBackupDir)
        {
            error = "檔案不在備份目錄內";
            return false;
        }

        if (!System.IO.File.Exists(fullCandidate))
        {
            error = "檔案不存在";
            return false;
        }

        fullPath = fullCandidate;
        return true;
    }
}
