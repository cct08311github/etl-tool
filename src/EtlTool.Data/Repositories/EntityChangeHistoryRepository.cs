using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

/// <summary>
/// 寫入 / 查詢 EntityChangeHistory（Connection / EtlTask 變更快照）。
///
/// 寫入點：
///   - ConnectionRepository.CreateAsync / UpdateAsync / DeleteAsync
///   - EtlTaskRepository.CreateAsync / UpdateAsync / DeleteAsync
/// 注意：呼叫端必須先 redact 連線字串、密碼等敏感欄位，再傳入 before/after。
/// </summary>
public sealed class EntityChangeHistoryRepository
{
    public const string ConnectionEntityType = "Connection";
    public const string EtlTaskEntityType = "EtlTask";

    private readonly AppDbContext _db;
    public EntityChangeHistoryRepository(AppDbContext db) { _db = db; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    /// <summary>
    /// 寫一筆變更歷史。before / after 任一可為 null（Created / Deleted）。
    /// 此方法**自帶 SaveChangesAsync**，獨立 transaction，不影響呼叫端。
    /// </summary>
    public async Task RecordAsync(
        string entityType, Guid entityId, string entityName,
        EntityChangeAction action,
        object? before, object? after,
        string? changedBy,
        CancellationToken ct)
    {
        var beforeJson = before is null ? null : JsonSerializer.Serialize(before, JsonOpts);
        var afterJson = after is null ? null : JsonSerializer.Serialize(after, JsonOpts);

        var rec = new EntityChangeHistory
        {
            EntityType = entityType,
            EntityId = entityId,
            EntityName = entityName,
            Action = action,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            ChangedBy = string.IsNullOrWhiteSpace(changedBy) ? "system" : changedBy,
            Summary = ComputeSummary(before, after, action),
        };
        _db.EntityChangeHistories.Add(rec);
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<EntityChangeHistory>> ListByEntityAsync(
        string entityType, Guid entityId, int take, CancellationToken ct)
        => _db.EntityChangeHistories.AsNoTracking()
            .Where(h => h.EntityType == entityType && h.EntityId == entityId)
            .OrderByDescending(h => h.ChangedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<List<EntityChangeHistory>> ListRecentAsync(int take, CancellationToken ct)
        => _db.EntityChangeHistories.AsNoTracking()
            .OrderByDescending(h => h.ChangedAt)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>
    /// 把兩個物件 reflect 比對，回傳「欄位 A→B」的人類可讀摘要。
    /// 用於 admin 列表的 quick view（不必展開 JSON 也能知道改了什麼）。
    /// </summary>
    public static string? ComputeSummary(object? before, object? after, EntityChangeAction action)
    {
        if (action == EntityChangeAction.Created) return "(新建)";
        if (action == EntityChangeAction.Deleted) return "(刪除)";
        if (before is null || after is null) return null;

        var beforeProps = before.GetType().GetProperties();
        var afterProps = after.GetType().GetProperties().ToDictionary(p => p.Name);

        var sb = new StringBuilder();
        foreach (var bp in beforeProps)
        {
            if (!afterProps.TryGetValue(bp.Name, out var ap)) continue;
            var bv = bp.GetValue(before);
            var av = ap.GetValue(after);
            if (!ValueEquals(bv, av))
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(bp.Name).Append(": ").Append(Render(bv)).Append('→').Append(Render(av));
            }
        }

        var s = sb.ToString();
        if (s.Length > 1900) s = s[..1900] + "…(截斷)";
        return s.Length == 0 ? "(無欄位差異 — 可能僅 UpdatedAt 變更)" : s;
    }

    private static bool ValueEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        // List/Array 用 JSON 比；其他用 Equals
        if (a is System.Collections.IEnumerable && a is not string)
        {
            return JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(b, JsonOpts);
        }
        return a.Equals(b);
    }

    private static string Render(object? v)
    {
        if (v is null) return "<null>";
        if (v is string s) return s.Length > 60 ? "\"" + s[..60] + "…\"" : "\"" + s + "\"";
        if (v is bool b) return b ? "true" : "false";
        if (v is System.Collections.IEnumerable enumerable && v is not string)
        {
            var count = 0;
            foreach (var _ in enumerable) count++;
            return $"[{count} item(s)]";
        }
        return v.ToString() ?? "";
    }
}
