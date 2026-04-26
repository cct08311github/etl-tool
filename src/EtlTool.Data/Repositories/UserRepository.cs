using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class UserRepository
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public UserRepository(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<List<User>> ListAsync(CancellationToken ct)
        => _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct)
        => _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<User?> GetAsync(Guid id, CancellationToken ct)
        => _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<int> CountAsync(CancellationToken ct)
        => _db.Users.CountAsync(ct);

    public async Task<User> CreateAsync(User user, string actor, CancellationToken ct)
    {
        user.CreatedAt = DateTime.UtcNow;
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Create,
            $"建立使用者「{user.Username}」(角色 {user.Role})",
            targetType: nameof(User), targetId: user.Id, targetName: user.Username,
            actor: actor, ct: ct);
        return user;
    }

    public async Task UpdateAsync(Guid id, UserRole role, bool isActive, string actor, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"User {id} not found.");
        var oldRole = u.Role;
        var oldActive = u.IsActive;
        u.Role = role;
        u.IsActive = isActive;
        await _db.SaveChangesAsync(ct);

        var changes = new List<string>();
        if (oldRole != role) changes.Add($"角色 {oldRole}→{role}");
        if (oldActive != isActive) changes.Add($"啟用 {oldActive}→{isActive}");
        var msg = $"更新使用者「{u.Username}」" + (changes.Count > 0 ? "：" + string.Join("、", changes) : "");
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            msg, targetType: nameof(User), targetId: id, targetName: u.Username,
            actor: actor, ct: ct);
    }

    public async Task ResetPasswordAsync(Guid id, string newHash, string actor, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"User {id} not found.");
        u.PasswordHash = newHash;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"重設使用者「{u.Username}」密碼",
            targetType: nameof(User), targetId: id, targetName: u.Username,
            severity: AuditSeverity.Warning, actor: actor, ct: ct);
    }

    public async Task DeleteAsync(Guid id, string actor, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) return;
        var name = u.Username;
        _db.Users.Remove(u);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Delete,
            $"刪除使用者「{name}」",
            targetType: nameof(User), targetId: id, targetName: name,
            severity: AuditSeverity.Warning, actor: actor, ct: ct);
    }

    public async Task UpdateLastLoginAsync(Guid id, CancellationToken ct)
    {
        await _db.Users
            .Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, DateTime.UtcNow), ct);
    }
}
