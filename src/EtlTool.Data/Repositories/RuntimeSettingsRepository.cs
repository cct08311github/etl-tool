using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

/// <summary>
/// 執行期可調設定的 read/write，每次寫入都加 audit。
/// 唯一表 RuntimeSettings 是 key-value，key = "Webhooks:OnFailure" 之類點分形式
/// （與 IConfiguration 同 key），方便將來把 IConfiguration 一起 overlay。
/// </summary>
public sealed class RuntimeSettingsRepository
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public RuntimeSettingsRepository(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
        => (await _db.RuntimeSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct)
        => await _db.RuntimeSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

    /// <summary>
    /// Upsert：若 key 已存在則更新；否則新增。每次寫入都記 audit。
    /// 為避免敏感字串（API key / signing secret）外洩到 audit log，呼叫端可傳
    /// <paramref name="redactValue"/>=true，audit 訊息只記「(已遮罩)」不記原值。
    /// </summary>
    public async Task SetAsync(string key, string value, string? actor, bool redactValue, CancellationToken ct)
    {
        var existing = await _db.RuntimeSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        var oldValue = existing?.Value;
        if (existing is null)
        {
            _db.RuntimeSettings.Add(new RuntimeSetting
            {
                Key = key, Value = value, UpdatedAt = DateTime.UtcNow, UpdatedBy = actor,
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = actor;
        }
        await _db.SaveChangesAsync(ct);

        var displayOld = redactValue ? Mask(oldValue) : (oldValue ?? "(null)");
        var displayNew = redactValue ? Mask(value) : value;
        await _audit.LogAsync(
            AuditCategory.System, AuditAction.Update,
            $"修改執行期設定 {key}：{displayOld} → {displayNew}",
            severity: AuditSeverity.Info, actor: actor, ct: ct);
    }

    public async Task DeleteAsync(string key, string? actor, CancellationToken ct)
    {
        var existing = await _db.RuntimeSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is null) return;
        _db.RuntimeSettings.Remove(existing);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditCategory.System, AuditAction.Delete,
            $"清除執行期設定 {key}（回退到 appsettings 預設值）",
            severity: AuditSeverity.Info, actor: actor, ct: ct);
    }

    /// <summary>給敏感欄位用：保留首尾 1 字 + ******。</summary>
    private static string Mask(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "(空)";
        if (v.Length <= 4) return new string('*', v.Length);
        return v[0] + new string('*', v.Length - 2) + v[^1];
    }
}
