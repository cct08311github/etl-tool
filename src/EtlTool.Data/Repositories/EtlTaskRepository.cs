using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class EtlTaskRepository : IEtlTaskLookup, IAllEtlTasksProvider
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly EntityChangeHistoryRepository _history;

    public EtlTaskRepository(AppDbContext db, IAuditLogger audit, EntityChangeHistoryRepository history)
    {
        _db = db; _audit = audit; _history = history;
    }

    public async Task<EtlTask?> GetWithMappingsAsync(Guid id, CancellationToken ct)
        => await _db.EtlTasks.AsNoTracking()
            .Include(t => t.Mappings)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<EtlTask>> GetAllAsync(CancellationToken ct)
        => await _db.EtlTasks.AsNoTracking()
            .Include(t => t.Mappings)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

    public Task<List<EtlTask>> ListLightweightAsync(CancellationToken ct)
        => _db.EtlTasks.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);

    public async Task<EtlTask> CreateAsync(EtlTask task, CancellationToken ct, string? actor = null)
    {
        task.CreatedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        _db.EtlTasks.Add(task);
        await _db.SaveChangesAsync(ct);

        await _history.RecordAsync(
            EntityChangeHistoryRepository.EtlTaskEntityType, task.Id, task.Name,
            EntityChangeAction.Created, before: null,
            after: Snapshot(task, task.Mappings?.ToList() ?? new List<ColumnMapping>()),
            changedBy: actor, ct: ct);

        await _audit.LogAsync(AuditCategory.Task, AuditAction.Create,
            $"建立任務「{task.Name}」（{task.WriteMode}）",
            targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
            actor: actor, ct: ct);
        return task;
    }

    public async Task<EtlTask> UpdateAsync(EtlTask task, CancellationToken ct, string? actor = null)
    {
        // 注意：刻意不 Include(Mappings)。
        // 過去版本同時 RemoveRange + 重指派導航屬性，會讓 EF 對同一列產生兩條 DELETE，
        // 第二條找不到列而拋 optimistic concurrency exception。
        var existing = await _db.EtlTasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct)
            ?? throw new InvalidOperationException($"Task {task.Id} not found.");

        // before snapshot — 先把舊版（含 mappings）整體抓下來
        var beforeMappings = await _db.ColumnMappings.AsNoTracking()
            .Where(m => m.EtlTaskId == task.Id)
            .OrderBy(m => m.OrderIndex).ThenBy(m => m.SourceColumn)
            .ToListAsync(ct);
        var before = Snapshot(existing, beforeMappings);

        existing.Name = task.Name;
        existing.Enabled = task.Enabled;
        existing.SourceConnectionId = task.SourceConnectionId;
        existing.SourceSchema = task.SourceSchema;
        existing.SourceTable = task.SourceTable;
        existing.TargetConnectionId = task.TargetConnectionId;
        existing.TargetSchema = task.TargetSchema;
        existing.TargetTable = task.TargetTable;
        existing.WriteMode = task.WriteMode;
        existing.FilterMode = task.FilterMode;
        existing.FilterFormJson = task.FilterFormJson;
        existing.FilterRawSql = task.FilterRawSql;
        existing.DeleteWhereSameAsFilter = task.DeleteWhereSameAsFilter;
        existing.DeleteWhereRawSql = task.DeleteWhereRawSql;
        existing.BatchSize = task.BatchSize;
        existing.CronExpression = task.CronExpression;
        existing.MaxRetries = task.MaxRetries;
        existing.RetryDelaySeconds = task.RetryDelaySeconds;
        existing.RetryBackoffMultiplier = task.RetryBackoffMultiplier;
        existing.PostSuccessSp = task.PostSuccessSp;
        existing.PostFailureSp = task.PostFailureSp;
        existing.MaskSamplePayload = task.MaskSamplePayload;
        existing.SchemaDriftPolicy = task.SchemaDriftPolicy;
        existing.SourceSchemaSnapshotJson = task.SourceSchemaSnapshotJson;
        existing.TargetSchemaSnapshotJson = task.TargetSchemaSnapshotJson;
        existing.SchemaSnapshotAt = task.SchemaSnapshotAt;
        existing.MinExpectedRows = task.MinExpectedRows;
        existing.MaxExpectedRows = task.MaxExpectedRows;
        existing.RowCountPolicy = task.RowCountPolicy;
        existing.RunHistoryRetentionRuns = task.RunHistoryRetentionRuns;
        existing.MaxRunMinutes = task.MaxRunMinutes;
        existing.AutoDisableAfterFailures = task.AutoDisableAfterFailures;
        existing.Notes = task.Notes;
        existing.DependsOnTaskIds = task.DependsOnTaskIds;
        existing.DependencyLookbackHours = task.DependencyLookbackHours;

        // 若這次 update 把 task 從 disabled 變回 enabled，且之前是 auto-disable 狀態，
        // 清掉 AutoDisabled 標記 — admin 確認過、決定重新啟用，狀態回到「正常 enabled」。
        if (task.Enabled && existing.AutoDisabledAt is not null)
        {
            existing.AutoDisabledAt = null;
            existing.AutoDisabledReason = null;
        }

        existing.UpdatedAt = DateTime.UtcNow;

        // 用 ExecuteDeleteAsync 直接下 SQL DELETE，繞過 change tracker
        await _db.ColumnMappings
            .Where(m => m.EtlTaskId == task.Id)
            .ExecuteDeleteAsync(ct);

        // 加新的（全新 Id，避免和已刪除的 tracker 衝突）
        var newMappings = task.Mappings.Select(m => new ColumnMapping
        {
            Id = Guid.NewGuid(),
            EtlTaskId = existing.Id,
            SourceColumn = m.SourceColumn,
            TargetColumn = m.TargetColumn,
            IsKey = m.IsKey,
            TransformExpression = m.TransformExpression,
            OrderIndex = m.OrderIndex,
        }).ToList();
        _db.ColumnMappings.AddRange(newMappings);

        await _db.SaveChangesAsync(ct);

        var after = Snapshot(existing, newMappings);
        await _history.RecordAsync(
            EntityChangeHistoryRepository.EtlTaskEntityType, existing.Id, existing.Name,
            EntityChangeAction.Updated, before, after, actor, ct);

        await _audit.LogAsync(AuditCategory.Task, AuditAction.Update,
            $"更新任務「{task.Name}」",
            targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
            actor: actor, ct: ct);

        // 回傳新讀取的版本（不含 tracker，避免外面再次操作意外）
        return await _db.EtlTasks.AsNoTracking()
            .Include(t => t.Mappings)
            .FirstAsync(t => t.Id == task.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct, string? actor = null)
    {
        var e = await _db.EtlTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (e is null) return;
        var name = e.Name;
        var beforeMappings = await _db.ColumnMappings.AsNoTracking()
            .Where(m => m.EtlTaskId == id)
            .OrderBy(m => m.OrderIndex).ThenBy(m => m.SourceColumn)
            .ToListAsync(ct);
        var before = Snapshot(e, beforeMappings);

        _db.EtlTasks.Remove(e);
        await _db.SaveChangesAsync(ct);

        await _history.RecordAsync(
            EntityChangeHistoryRepository.EtlTaskEntityType, id, name,
            EntityChangeAction.Deleted, before, after: null, changedBy: actor, ct: ct);

        await _audit.LogAsync(AuditCategory.Task, AuditAction.Delete,
            $"刪除任務「{name}」",
            targetType: nameof(EtlTask), targetId: id, targetName: name,
            severity: AuditSeverity.Warning, actor: actor, ct: ct);
    }

    /// <summary>
    /// EtlTask 的 history snapshot — 包含完整 task 設定 + mappings。
    /// **不含**密碼類欄位（task 本身沒有），但 schema snapshot JSON 可能很大 → 截斷至 4KB。
    /// </summary>
    private record TaskSnapshot(
        Guid Id, string Name, bool Enabled,
        Guid SourceConnectionId, string SourceSchema, string SourceTable,
        Guid TargetConnectionId, string TargetSchema, string TargetTable,
        WriteMode WriteMode, FilterMode FilterMode,
        string? FilterFormJson, string? FilterRawSql,
        bool DeleteWhereSameAsFilter, string? DeleteWhereRawSql,
        int BatchSize, string CronExpression,
        int MaxRetries, int RetryDelaySeconds, double RetryBackoffMultiplier,
        string? PostSuccessSp, string? PostFailureSp,
        bool MaskSamplePayload,
        SchemaDriftPolicy SchemaDriftPolicy,
        long? MinExpectedRows, long? MaxExpectedRows, EtlTool.Core.Engine.RowCountAssertionPolicy RowCountPolicy,
        int? RunHistoryRetentionRuns,
        int? MaxRunMinutes,
        int? AutoDisableAfterFailures,
        List<MappingSnapshot> Mappings);

    private record MappingSnapshot(
        string SourceColumn, string TargetColumn, bool IsKey,
        string? TransformExpression, int OrderIndex);

    private static TaskSnapshot Snapshot(EtlTask t, IReadOnlyList<ColumnMapping> mappings) => new(
        t.Id, t.Name, t.Enabled,
        t.SourceConnectionId, t.SourceSchema, t.SourceTable,
        t.TargetConnectionId, t.TargetSchema, t.TargetTable,
        t.WriteMode, t.FilterMode,
        t.FilterFormJson, t.FilterRawSql,
        t.DeleteWhereSameAsFilter, t.DeleteWhereRawSql,
        t.BatchSize, t.CronExpression,
        t.MaxRetries, t.RetryDelaySeconds, t.RetryBackoffMultiplier,
        t.PostSuccessSp, t.PostFailureSp,
        t.MaskSamplePayload,
        t.SchemaDriftPolicy,
        t.MinExpectedRows, t.MaxExpectedRows, t.RowCountPolicy,
        t.RunHistoryRetentionRuns,
        t.MaxRunMinutes,
        t.AutoDisableAfterFailures,
        mappings.Select(m => new MappingSnapshot(
            m.SourceColumn, m.TargetColumn, m.IsKey, m.TransformExpression, m.OrderIndex)).ToList());
}
