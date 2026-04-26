using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlTool.Data.Repositories;

/// <summary>
/// EF Core 落地 AuditEvent 的實作。
/// 設計準則：
///   1) 寫 audit 失敗只 log 不 throw — 不可影響主流程
///   2) 用獨立的 DI scope 取得 DbContext，避免污染呼叫者的 change tracker
///      （特別是當呼叫者已經在 SaveChanges 期間時）
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogger> _log;

    public AuditLogger(IServiceScopeFactory scopeFactory, ILogger<AuditLogger> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task LogAsync(
        AuditCategory category,
        AuditAction action,
        string message,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        AuditSeverity severity = AuditSeverity.Info,
        string? detailsJson = null,
        string? actor = null,
        CancellationToken ct = default)
    {
        var evt = new AuditEvent
        {
            At = DateTime.UtcNow,
            Category = category,
            Action = action,
            Severity = severity,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = Truncate(targetName, 200),
            Actor = actor ?? "system",
            Message = Truncate(message, 500) ?? string.Empty,
            DetailsJson = detailsJson,
        };

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditEvents.Add(evt);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist audit event ({Category}/{Action}): {Message}",
                category, action, message);
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}

public sealed class AuditQueryRepository
{
    private readonly AppDbContext _db;
    public AuditQueryRepository(AppDbContext db) { _db = db; }

    public sealed record AuditQuery(
        DateTime? Since = null,
        DateTime? Until = null,
        AuditCategory? Category = null,
        AuditSeverity? MinSeverity = null,
        string? Search = null,
        int Skip = 0,
        int Take = 50);

    public async Task<(List<AuditEvent> Items, int Total)> ListAsync(AuditQuery q, CancellationToken ct)
    {
        IQueryable<AuditEvent> baseQuery = _db.AuditEvents.AsNoTracking();

        if (q.Since is { } since) baseQuery = baseQuery.Where(e => e.At >= since);
        if (q.Until is { } until) baseQuery = baseQuery.Where(e => e.At <= until);
        if (q.Category is { } cat) baseQuery = baseQuery.Where(e => e.Category == cat);
        if (q.MinSeverity is { } sev) baseQuery = baseQuery.Where(e => e.Severity >= sev);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search!;
            baseQuery = baseQuery.Where(e =>
                EF.Functions.Like(e.Message, "%" + s + "%") ||
                (e.TargetName != null && EF.Functions.Like(e.TargetName, "%" + s + "%")));
        }

        var total = await baseQuery.CountAsync(ct);
        var items = await baseQuery
            .OrderByDescending(e => e.At)
            .Skip(q.Skip)
            .Take(q.Take)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<AuditEvent?> GetAsync(Guid id, CancellationToken ct)
        => _db.AuditEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
}
