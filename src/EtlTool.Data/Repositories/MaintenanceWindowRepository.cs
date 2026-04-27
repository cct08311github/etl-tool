using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EtlTool.Data.Repositories;

/// <summary>
/// MaintenanceWindow CRUD + 合併 appsettings 來源的 IMaintenanceWindowProvider 實作。
/// </summary>
public sealed class MaintenanceWindowRepository : IMaintenanceWindowProvider
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly IOptionsMonitor<MaintenanceWindowsOptions> _appsettings;

    public MaintenanceWindowRepository(
        AppDbContext db, IAuditLogger audit,
        IOptionsMonitor<MaintenanceWindowsOptions> appsettings)
    {
        _db = db;
        _audit = audit;
        _appsettings = appsettings;
    }

    public Task<List<MaintenanceWindowEntity>> ListAsync(CancellationToken ct)
        => _db.MaintenanceWindows.AsNoTracking().OrderBy(w => w.From).ToListAsync(ct);

    public async Task<MaintenanceWindowEntity> CreateAsync(
        MaintenanceWindowEntity w, string? actor, CancellationToken ct)
    {
        w.CreatedAt = DateTime.UtcNow;
        w.UpdatedAt = DateTime.UtcNow;
        _db.MaintenanceWindows.Add(w);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.System, AuditAction.Create,
            $"新增維護視窗 {w.Days} {w.From}-{w.To}（{w.Reason ?? "無理由"}, enabled={w.Enabled}）",
            severity: AuditSeverity.Info, actor: actor, ct: ct);
        return w;
    }

    public async Task UpdateAsync(MaintenanceWindowEntity w, string? actor, CancellationToken ct)
    {
        var existing = await _db.MaintenanceWindows.FirstOrDefaultAsync(x => x.Id == w.Id, ct)
            ?? throw new InvalidOperationException($"MaintenanceWindow {w.Id} not found.");
        existing.Days = w.Days;
        existing.From = w.From;
        existing.To = w.To;
        existing.Reason = w.Reason;
        existing.Enabled = w.Enabled;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.System, AuditAction.Update,
            $"更新維護視窗 {existing.Days} {existing.From}-{existing.To} (enabled={existing.Enabled})",
            severity: AuditSeverity.Info, actor: actor, ct: ct);
    }

    public async Task DeleteAsync(Guid id, string? actor, CancellationToken ct)
    {
        var w = await _db.MaintenanceWindows.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return;
        var label = $"{w.Days} {w.From}-{w.To}";
        _db.MaintenanceWindows.Remove(w);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.System, AuditAction.Delete,
            $"刪除維護視窗 {label}", severity: AuditSeverity.Warning, actor: actor, ct: ct);
    }

    public async Task<MaintenanceWindow?> CurrentlyActiveAsync(DateTime localNow, CancellationToken ct)
    {
        // 先檢查 appsettings 的（不需要 DB query）
        var fromAppsettings = _appsettings.CurrentValue.CurrentlyActive(localNow);
        if (fromAppsettings is not null) return fromAppsettings;

        // 再檢查 DB rows（只取 Enabled=true）
        var rows = await _db.MaintenanceWindows
            .AsNoTracking()
            .Where(w => w.Enabled)
            .ToListAsync(ct);

        foreach (var r in rows)
        {
            var mw = ToMaintenanceWindow(r);
            if (mw.IsActive(localNow)) return mw;
        }
        return null;
    }

    public async Task<IReadOnlyList<MergedMaintenanceWindow>> ListAllAsync(CancellationToken ct)
    {
        var result = new List<MergedMaintenanceWindow>();

        // appsettings 來源 — 一律視為 enabled（appsettings 移除即停用）
        foreach (var w in _appsettings.CurrentValue.Windows)
        {
            result.Add(new MergedMaintenanceWindow(
                DbId: null, Source: "appsettings",
                Days: w.Days, From: w.From, To: w.To, Reason: w.Reason, Enabled: true));
        }

        // DB 來源
        var rows = await _db.MaintenanceWindows.AsNoTracking().OrderBy(w => w.From).ToListAsync(ct);
        foreach (var r in rows)
        {
            var days = (r.Days ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .ToArray();
            result.Add(new MergedMaintenanceWindow(
                DbId: r.Id, Source: "database",
                Days: days, From: r.From, To: r.To, Reason: r.Reason, Enabled: r.Enabled));
        }
        return result;
    }

    private static MaintenanceWindow ToMaintenanceWindow(MaintenanceWindowEntity r)
        => new()
        {
            Days = (r.Days ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .ToArray(),
            From = r.From,
            To = r.To,
            Reason = r.Reason,
        };
}
