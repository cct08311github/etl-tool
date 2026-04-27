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
/// Export all EtlTasks as CSV in the same shape that TaskCsvImporter accepts.
/// Round-trip: export → edit in Excel → re-import.
///
/// Special handler ?template=1 returns a 1-row example CSV for admin to base
/// new imports on.
/// </summary>
[Authorize(Roles = "Admin,Operator")]
public class TasksExportModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public TasksExportModel(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IActionResult> OnGetAsync(bool template, CancellationToken ct)
    {
        if (template)
        {
            var tpl = TaskCsvImporter.CanonicalHeader + "\n" +
                "OrdersDaily,prod-mssql,dbo,Orders_SRC,prod-oracle,HR,Orders_TGT,DeleteInsert,0 0 2 * * ?,true,\"daily,critical\"";
            var bytes = Encoding.UTF8.GetBytes(tpl);
            return File(bytes, "text/csv; charset=utf-8", "etltool-tasks-template.csv");
        }

        var tasks = await _db.EtlTasks.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var conns = await _db.Connections.AsNoTracking()
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var connNameById = conns.ToDictionary(c => c.Id, c => c.Name);

        var csv = TaskCsvImporter.Render(tasks, connNameById);
        var actor = User.Identity?.Name;
        await _audit.LogAsync(AuditCategory.Task, AuditAction.Update,
            $"📥 匯出所有任務 CSV（{tasks.Count} 個任務）",
            severity: AuditSeverity.Info, actor: actor, ct: ct);

        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8",
            $"etltool-tasks-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }
}
