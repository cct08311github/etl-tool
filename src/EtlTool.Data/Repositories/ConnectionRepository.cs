using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class ConnectionRepository : IConnectionLookup
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    public ConnectionRepository(AppDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    public Task<ConnectionDefinition?> GetAsync(Guid id, CancellationToken ct)
        => _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<List<ConnectionDefinition>> ListAsync(CancellationToken ct)
        => _db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public async Task<ConnectionDefinition> CreateAsync(
        string name, DbProviderType providerType, string plainConnectionString,
        IConnectionStringProtector protector, CancellationToken ct)
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
        await _audit.LogAsync(AuditCategory.Connection, AuditAction.Create,
            $"建立連線「{name}」（{providerType}）",
            targetType: nameof(ConnectionDefinition), targetId: entity.Id, targetName: name, ct: ct);
        return entity;
    }

    public async Task UpdateAsync(
        Guid id, string name, DbProviderType providerType, string? newPlainConnectionString,
        IConnectionStringProtector protector, CancellationToken ct)
    {
        var e = await _db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException($"Connection {id} not found.");
        e.Name = name;
        e.ProviderType = providerType;
        var passwordChanged = !string.IsNullOrEmpty(newPlainConnectionString);
        if (passwordChanged)
            e.EncryptedConnectionString = protector.Protect(newPlainConnectionString!);
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Connection, AuditAction.Update,
            $"更新連線「{name}」" + (passwordChanged ? "（含連線字串變更）" : ""),
            targetType: nameof(ConnectionDefinition), targetId: id, targetName: name, ct: ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var e = await _db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return;
        var name = e.Name;
        _db.Connections.Remove(e);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Connection, AuditAction.Delete,
            $"刪除連線「{name}」",
            targetType: nameof(ConnectionDefinition), targetId: id, targetName: name,
            severity: AuditSeverity.Warning, ct: ct);
    }

    public async Task<string> GetPlainConnectionStringAsync(Guid id, IConnectionStringProtector protector, CancellationToken ct)
    {
        var e = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException($"Connection {id} not found.");
        return protector.Unprotect(e.EncryptedConnectionString);
    }
}
