using System.Text;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.App.Pages.Account;

[Authorize(Roles = "Admin")]
public class AuditExportModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public AuditExportModel(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public string DefaultFrom => DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
    public string DefaultTo => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public IActionResult OnGet() => Page();

    public async Task<IActionResult> OnGetDownloadAsync(string? from, string? to, CancellationToken ct)
    {
        // 解析日期；無效退化為近 30 天
        DateTime fromUtc, toUtc;
        if (!DateTime.TryParse(from, out fromUtc)) fromUtc = DateTime.UtcNow.AddDays(-30);
        if (!DateTime.TryParse(to, out toUtc)) toUtc = DateTime.UtcNow;
        // 把 to 拉到當天 23:59:59 UTC
        toUtc = toUtc.Date.AddDays(1).AddTicks(-1);
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);

        // 抓 events — 升序，這樣 FirstHash/LastHash 才對應時序
        var events = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.At >= fromUtc && e.At <= toUtc)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToListAsync(ct);

        // 匯出
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(true), leaveOpen: true))
        {
            var cert = await AuditExporter.WriteCsvAsync(events, writer, ct);
            await AuditExporter.WriteCertificateFooterAsync(cert, writer);
            await writer.FlushAsync();
        }

        // Audit：誰下載了什麼範圍
        var actor = User.Identity?.Name;
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"下載稽核日誌 CSV：{fromUtc:yyyy-MM-dd} ~ {toUtc:yyyy-MM-dd}（{events.Count} 筆）",
            severity: AuditSeverity.Info, actor: actor, ct: ct);

        var fileName = $"audit-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}.csv";
        ms.Position = 0;
        return File(ms.ToArray(), "text/csv; charset=utf-8", fileName);
    }
}
