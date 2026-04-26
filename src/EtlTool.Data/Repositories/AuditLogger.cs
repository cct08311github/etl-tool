using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlTool.Data.Repositories;

/// <summary>
/// EF Core 落地 AuditEvent 的實作。Singleton。
/// 設計準則：
///   1) 寫 audit 失敗只 log 不 throw — 不可影響主流程
///   2) 用獨立的 DI scope 取得 DbContext，避免污染呼叫者的 change tracker
///   3) Hash chain：每筆 audit hash = SHA256(prevHash || canonical fields)
///      用 SemaphoreSlim 序列化 inserts，確保 chain 順序正確
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogger> _log;
    // 全程序唯一 — 序列化 audit insert 以維持 hash chain 順序
    private static readonly SemaphoreSlim _chainLock = new(1, 1);

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

        await _chainLock.WaitAsync(ct);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 撈最後一筆的 hash 作為 prev
            var prev = await db.AuditEvents
                .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync(ct);

            evt.PreviousHash = prev;
            evt.Hash = AuditHasher.ComputeHash(evt, prev);

            db.AuditEvents.Add(evt);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist audit event ({Category}/{Action}): {Message}",
                category, action, message);
        }
        finally
        {
            _chainLock.Release();
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>
/// 走訪整個 audit log，重算每筆 hash 與 stored 比對，回報第一處 tampering 位置。
/// 用於合規檢查 / 事故調查。
/// </summary>
public sealed class AuditChainVerifier
{
    private readonly AppDbContext _db;
    public AuditChainVerifier(AppDbContext db) { _db = db; }

    public sealed record VerifyResult(
        bool IsIntact,
        long TotalChecked,
        AuditEvent? FirstBadEvent,
        string? Reason);

    public async Task<VerifyResult> VerifyAsync(CancellationToken ct)
    {
        // 全表流式驗證；At 升序、Id 升序作為 stable tiebreaker
        var query = _db.AuditEvents
            .AsNoTracking()
            .OrderBy(e => e.At).ThenBy(e => e.Id);

        long n = 0;
        string? prev = null;

        await foreach (var e in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            n++;

            // 1. PreviousHash 應該等於上一筆的 Hash
            if (!string.Equals(e.PreviousHash ?? "", prev ?? "", StringComparison.Ordinal))
            {
                return new VerifyResult(false, n, e,
                    $"PreviousHash 與上一筆 Hash 不符（第 {n} 筆）：" +
                    $"expected '{prev ?? "<null>"}'，stored '{e.PreviousHash ?? "<null>"}'");
            }

            // 2. 重算 hash 應該等於 stored Hash
            var recomputed = AuditHasher.ComputeHash(e, prev);
            if (!string.Equals(recomputed, e.Hash, StringComparison.Ordinal))
            {
                return new VerifyResult(false, n, e,
                    $"Hash 重算不符（第 {n} 筆）：" +
                    $"recomputed '{recomputed}'，stored '{e.Hash ?? "<null>"}' — " +
                    "可能此筆內容被竄改");
            }

            prev = e.Hash;
        }

        return new VerifyResult(true, n, null, null);
    }
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
