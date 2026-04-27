using System.Text;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Pages.Account;

/// <summary>
/// CSV export of RunHistory for a single task.
/// /Account/RunHistoryExport/{taskId}?from=2026-04-01&amp;to=2026-04-30
/// </summary>
[Authorize(Roles = "Admin,Operator")]
public class RunHistoryExportModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public RunHistoryExportModel(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> OnGetAsync(Guid taskId, string? from, string? to, CancellationToken ct)
    {
        var task = await _db.EtlTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return NotFound();

        DateTime fromUtc, toUtc;
        if (!DateTime.TryParse(from, out fromUtc)) fromUtc = DateTime.UtcNow.AddDays(-90);
        if (!DateTime.TryParse(to, out toUtc)) toUtc = DateTime.UtcNow;
        toUtc = toUtc.Date.AddDays(1).AddTicks(-1);
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);

        var runs = await _db.RunHistories.AsNoTracking()
            .Where(r => r.EtlTaskId == taskId && r.StartedAt >= fromUtc && r.StartedAt <= toUtc)
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(true), leaveOpen: true))
        {
            await writer.WriteLineAsync("StartedAt,FinishedAt,DurationSec,Status,TriggerType,RowsRead,RowsWritten,ErrorMessage");
            foreach (var r in runs)
            {
                var dur = r.FinishedAt is null ? 0 : (r.FinishedAt.Value - r.StartedAt).TotalSeconds;
                await writer.WriteLineAsync(string.Join(',',
                    AuditExporter.CsvEscape(r.StartedAt.ToString("o")),
                    AuditExporter.CsvEscape(r.FinishedAt?.ToString("o") ?? ""),
                    dur.ToString("F1"),
                    r.Status.ToString(),
                    r.TriggerType.ToString(),
                    r.RowsRead.ToString(),
                    r.RowsWritten.ToString(),
                    AuditExporter.CsvEscape(r.ErrorMessage ?? "")));
            }
            await writer.FlushAsync();
        }

        var actor = User.Identity?.Name;
        await _audit.LogAsync(AuditCategory.Task, AuditAction.Update,
            $"📥 下載任務「{task.Name}」執行歷史 CSV：{fromUtc:yyyy-MM-dd}~{toUtc:yyyy-MM-dd}（{runs.Count} 筆）",
            targetType: nameof(EtlTask), targetId: taskId, targetName: task.Name,
            severity: AuditSeverity.Info, actor: actor, ct: ct);

        var safeName = System.Text.RegularExpressions.Regex.Replace(task.Name, @"[^A-Za-z0-9_-]+", "_");
        var fileName = $"runs-{safeName}-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.csv";
        return File(ms.ToArray(), "text/csv; charset=utf-8", fileName);
    }
}
