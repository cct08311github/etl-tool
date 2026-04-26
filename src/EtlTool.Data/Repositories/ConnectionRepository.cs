using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class ConnectionRepository : IConnectionLookup
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly EntityChangeHistoryRepository _history;
    public ConnectionRepository(AppDbContext db, IAuditLogger audit, EntityChangeHistoryRepository history)
    {
        _db = db; _audit = audit; _history = history;
    }

    public Task<ConnectionDefinition?> GetAsync(Guid id, CancellationToken ct)
        => _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<List<ConnectionDefinition>> ListAsync(CancellationToken ct)
        => _db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public async Task<ConnectionDefinition> CreateAsync(
        string name, DbProviderType providerType, string plainConnectionString,
        IConnectionStringProtector protector, CancellationToken ct,
        string? actor = null)
    {
        var entity = new ConnectionDefinition
        {
            Name = name,
            ProviderType = providerType,
            EncryptedConnectionString = protector.Protect(plainConnectionString),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Connections.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _history.RecordAsync(
            EntityChangeHistoryRepository.ConnectionEntityType, entity.Id, name,
            EntityChangeAction.Created, before: null, after: Snapshot(entity), changedBy: actor, ct: ct);

        await _audit.LogAsync(AuditCategory.Connection, AuditAction.Create,
            $"建立連線「{name}」（{providerType}）",
            targetType: nameof(ConnectionDefinition), targetId: entity.Id, targetName: name,
            actor: actor, ct: ct);
        return entity;
    }

    public async Task UpdateAsync(
        Guid id, string name, DbProviderType providerType, string? newPlainConnectionString,
        IConnectionStringProtector protector, CancellationToken ct,
        string? actor = null)
    {
        var e = await _db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException($"Connection {id} not found.");

        var before = Snapshot(e);

        e.Name = name;
        e.ProviderType = providerType;
        var passwordChanged = !string.IsNullOrEmpty(newPlainConnectionString);
        if (passwordChanged)
            e.EncryptedConnectionString = protector.Protect(newPlainConnectionString!);
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var after = Snapshot(e);
        if (passwordChanged) after = after with { ConnectionStringChangedThisUpdate = true };

        await _history.RecordAsync(
            EntityChangeHistoryRepository.ConnectionEntityType, e.Id, e.Name,
            EntityChangeAction.Updated, before, after, actor, ct);

        await _audit.LogAsync(AuditCategory.Connection, AuditAction.Update,
            $"更新連線「{name}」" + (passwordChanged ? "（含連線字串變更）" : ""),
            targetType: nameof(ConnectionDefinition), targetId: id, targetName: name,
            actor: actor, ct: ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct, string? actor = null)
    {
        var e = await _db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return;
        var name = e.Name;
        var before = Snapshot(e);
        _db.Connections.Remove(e);
        await _db.SaveChangesAsync(ct);

        await _history.RecordAsync(
            EntityChangeHistoryRepository.ConnectionEntityType, id, name,
            EntityChangeAction.Deleted, before, after: null, changedBy: actor, ct: ct);

        await _audit.LogAsync(AuditCategory.Connection, AuditAction.Delete,
            $"刪除連線「{name}」",
            targetType: nameof(ConnectionDefinition), targetId: id, targetName: name,
            severity: AuditSeverity.Warning, actor: actor, ct: ct);
    }

    public async Task<string> GetPlainConnectionStringAsync(Guid id, IConnectionStringProtector protector, CancellationToken ct)
    {
        var e = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException($"Connection {id} not found.");
        return protector.Unprotect(e.EncryptedConnectionString);
    }

    /// <summary>
    /// 變更歷史用的 snapshot — **故意不含 EncryptedConnectionString**（避免 JSON 化後讓不該看的人看到 cipher，
    /// 也避免長 blob 撐爆 history 表）。改用 ConnectionStringChangedThisUpdate 標記是否有改連線字串。
    /// </summary>
    private record ConnSnapshot(
        Guid Id, string Name, DbProviderType ProviderType,
        DateTime CreatedAt, DateTime UpdatedAt,
        bool ConnectionStringChangedThisUpdate = false);

    private static ConnSnapshot Snapshot(ConnectionDefinition c) =>
        new(c.Id, c.Name, c.ProviderType, c.CreatedAt, c.UpdatedAt);
}
