using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EtlTool.App.Pages.Account;

[Authorize(Roles = "Admin")]
public class SqliteBackupModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly ILogger<SqliteBackupModel> _log;

    public SqliteBackupModel(AppDbContext db, IAuditLogger audit, ILogger<SqliteBackupModel> log)
    {
        _db = db;
        _audit = audit;
        _log = log;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        // 取目前 SQLite path
        var connStr = _db.Database.GetConnectionString() ?? "";
        var dataSrc = connStr
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase));
        if (dataSrc is null)
            return Content("無法解析 SQLite 路徑", "text/plain");

        var sqlitePath = dataSrc["Data Source=".Length..].Trim('"', ' ');
        if (!System.IO.File.Exists(sqlitePath))
            return Content($"資料庫檔不存在：{sqlitePath}", "text/plain");

        // 把備份檔放在 process temp，下載結束後刪除
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var tempPath = Path.Combine(Path.GetTempPath(), $"etltool-{stamp}-{Guid.NewGuid():N}.db");

        try
        {
            // 在 SQLite 上跑 VACUUM INTO — 這是 atomic、產生完整 consistent 備份
            // 不會 lock 主庫、不需停止服務。
            using (var cmd = _db.Database.GetDbConnection().CreateCommand())
            {
                if (_db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
                    await _db.Database.GetDbConnection().OpenAsync(ct);
                cmd.CommandText = "VACUUM INTO @target";
                var p = cmd.CreateParameter();
                p.ParameterName = "@target";
                p.Value = tempPath;
                cmd.Parameters.Add(p);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var size = new FileInfo(tempPath).Length;
            var actor = User.Identity?.Name;
            await _audit.LogAsync(AuditCategory.System, AuditAction.Update,
                $"📦 下載 SQLite 備份檔（{size / 1024.0:F0} KB，VACUUM INTO 一致性快照）",
                severity: AuditSeverity.Info, actor: actor, ct: ct);

            // 串流檔案內容並在 response 結束後刪 temp
            var bytes = await System.IO.File.ReadAllBytesAsync(tempPath, ct);
            var fileName = $"etltool-backup-{stamp}.db";
            return File(bytes, "application/x-sqlite3", fileName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SQLite backup failed");
            await _audit.LogAsync(AuditCategory.System, AuditAction.Update,
                $"⚠ SQLite 備份失敗：{ex.Message}",
                severity: AuditSeverity.Warning, actor: User.Identity?.Name, ct: ct);
            return Content($"備份失敗：{ex.Message}", "text/plain");
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); } catch { }
        }
    }
}
