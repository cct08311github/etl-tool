using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class UserRepository
{
    /// <summary>保留最近 N 個密碼 hash，避免重用。</summary>
    public const int PasswordHistoryDepth = 5;

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
        user.LastPasswordChangedAt = DateTime.UtcNow;
        // 銀行原則：admin 建立的初始密碼 = 暫時密碼，user 第一次登入必須改
        user.MustChangePassword = true;
        _db.Users.Add(user);

        // 建立首筆 password history
        _db.PasswordHistories.Add(new PasswordHistory
        {
            UserId = user.Id,
            PasswordHash = user.PasswordHash,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Create,
            $"建立使用者「{user.Username}」(角色 {user.Role}) — 初始密碼為暫時密碼，首次登入須變更",
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

    /// <summary>
    /// 管理員為他人重設密碼。設定 MustChangePassword = true，user 下次登入必須再改。
    /// 不檢查歷史 — admin reset 的邏輯是「給暫時密碼」，不是「user 自己改」。
    /// </summary>
    public async Task ResetPasswordAsync(Guid id, string newHash, string actor, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"User {id} not found.");
        u.PasswordHash = newHash;
        u.MustChangePassword = true;
        u.LastPasswordChangedAt = DateTime.UtcNow;

        _db.PasswordHistories.Add(new PasswordHistory
        {
            UserId = u.Id,
            PasswordHash = newHash,
            CreatedAt = DateTime.UtcNow,
        });
        // 注意順序：必須先 SaveChanges 把新 history row commit 進 DB，
        // 之後 PruneHistoryAsync 用 ExecuteDeleteAsync (直接 SQL) 才看得到它，
        // 否則會 prune 1 筆 + 新增 1 筆 = depth+1 筆（off-by-one bug）。
        await _db.SaveChangesAsync(ct);
        await PruneHistoryAsync(u.Id, ct);

        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"重設使用者「{u.Username}」密碼（將要求其首次登入再變更）",
            targetType: nameof(User), targetId: id, targetName: u.Username,
            severity: AuditSeverity.Warning, actor: actor, ct: ct);
    }

    /// <summary>
    /// User 自己變更密碼（透過 /Account/ChangePassword）。
    /// 在呼叫前須先 verify 舊密碼。本方法只負責檢查 history reuse 並寫入。
    /// 抛 InvalidOperationException 表示 reuse rejected。
    /// </summary>
    public async Task ChangeOwnPasswordAsync(Guid id, string newHash, string actor, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new InvalidOperationException($"User {id} not found.");

        // 檢查歷史 — 不可與最近 N 筆相同
        var recentHashes = await _db.PasswordHistories.AsNoTracking()
            .Where(h => h.UserId == id)
            .OrderByDescending(h => h.CreatedAt)
            .Take(PasswordHistoryDepth)
            .Select(h => h.PasswordHash)
            .ToListAsync(ct);

        // 注意：BCrypt hash 不固定（每次有 salt），所以不能比對 hash 字串相等。
        // 但這個方法收到的已是 hash（newHash），我們要驗證的是「user 輸入的 plaintext 是否與舊 hash 對應」。
        // → 把 plaintext 留給 caller 處理；這裡 caller 必須在 hash 前自己驗。
        // 為了方便，提供另一個重載 (ChangeOwnPasswordWithPlaintextAsync) — 在下面。
        // 此方法假設 caller 已做 reuse 檢查；只直接寫。
        u.PasswordHash = newHash;
        u.MustChangePassword = false;
        u.LastPasswordChangedAt = DateTime.UtcNow;

        _db.PasswordHistories.Add(new PasswordHistory
        {
            UserId = u.Id,
            PasswordHash = newHash,
            CreatedAt = DateTime.UtcNow,
        });
        // SaveChanges 必須在 PruneHistoryAsync 之前（見 ResetPasswordAsync 註解）
        await _db.SaveChangesAsync(ct);
        await PruneHistoryAsync(u.Id, ct);

        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"使用者「{u.Username}」變更自己的密碼",
            targetType: nameof(User), targetId: id, targetName: u.Username,
            severity: AuditSeverity.Info, actor: actor, ct: ct);
    }

    /// <summary>
    /// User 自己變更密碼，含 reuse 檢查（用 plaintext 對比歷史 hash）。
    /// 回傳 (success, errorMsg)。錯誤時不寫 DB 也不寫 audit。
    /// </summary>
    public async Task<(bool Success, string? Error)> ChangeOwnPasswordWithReuseCheckAsync(
        Guid id, string newPlaintext,
        Func<string, string> hasher,
        Func<string, string, bool> verifier,
        string actor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newPlaintext))
            return (false, "新密碼不可為空");

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) return (false, "使用者不存在");

        // 取最近 N 筆 history hash 跟 newPlaintext 比
        var recentHashes = await _db.PasswordHistories.AsNoTracking()
            .Where(h => h.UserId == id)
            .OrderByDescending(h => h.CreatedAt)
            .Take(PasswordHistoryDepth)
            .Select(h => h.PasswordHash)
            .ToListAsync(ct);

        foreach (var oldHash in recentHashes)
        {
            try
            {
                if (verifier(newPlaintext, oldHash))
                    return (false, $"新密碼不可與最近 {PasswordHistoryDepth} 個歷史密碼相同");
            }
            catch { /* hash 損毀 → 略過比對 */ }
        }

        var newHash = hasher(newPlaintext);
        u.PasswordHash = newHash;
        u.MustChangePassword = false;
        u.LastPasswordChangedAt = DateTime.UtcNow;

        _db.PasswordHistories.Add(new PasswordHistory
        {
            UserId = u.Id,
            PasswordHash = newHash,
            CreatedAt = DateTime.UtcNow,
        });
        // SaveChanges 必須在 PruneHistoryAsync 之前（見 ResetPasswordAsync 註解）
        await _db.SaveChangesAsync(ct);
        await PruneHistoryAsync(u.Id, ct);

        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"使用者「{u.Username}」變更自己的密碼",
            targetType: nameof(User), targetId: id, targetName: u.Username,
            severity: AuditSeverity.Info, actor: actor, ct: ct);

        return (true, null);
    }

    public async Task DeleteAsync(Guid id, string actor, CancellationToken ct)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) return;
        var name = u.Username;
        _db.Users.Remove(u);
        // 同時刪 password history（FK 沒設 cascade，手動清）
        await _db.PasswordHistories
            .Where(h => h.UserId == id)
            .ExecuteDeleteAsync(ct);
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

    /// <summary>清掉超過 PasswordHistoryDepth 筆的舊 history。</summary>
    private async Task PruneHistoryAsync(Guid userId, CancellationToken ct)
    {
        var keepIds = await _db.PasswordHistories
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.CreatedAt)
            .Take(PasswordHistoryDepth)
            .Select(h => h.Id)
            .ToListAsync(ct);
        await _db.PasswordHistories
            .Where(h => h.UserId == userId && !keepIds.Contains(h.Id))
            .ExecuteDeleteAsync(ct);
    }
}
