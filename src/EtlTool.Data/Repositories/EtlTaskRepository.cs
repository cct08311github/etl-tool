using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class EtlTaskRepository : IEtlTaskLookup, IAllEtlTasksProvider
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    public EtlTaskRepository(AppDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

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

    public async Task<EtlTask> CreateAsync(EtlTask task, CancellationToken ct)
    {
        task.CreatedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        _db.EtlTasks.Add(task);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Task, AuditAction.Create,
            $"建立任務「{task.Name}」（{task.WriteMode}）",
            targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name, ct: ct);
        return task;
    }

    public async Task<EtlTask> UpdateAsync(EtlTask task, CancellationToken ct)
    {
        // 注意：刻意不 Include(Mappings)。
        // 過去版本同時 RemoveRange + 重指派導航屬性，會讓 EF 對同一列產生兩條 DELETE，
        // 第二條找不到列而拋 optimistic concurrency exception。
        var existing = await _db.EtlTasks.FirstOrDefaultAsync(t => t.Id == task.Id, ct)
            ?? throw new InvalidOperationException($"Task {task.Id} not found.");

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

        await _audit.LogAsync(AuditCategory.Task, AuditAction.Update,
            $"更新任務「{task.Name}」",
            targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name, ct: ct);

        // 回傳新讀取的版本（不含 tracker，避免外面再次操作意外）
        return await _db.EtlTasks.AsNoTracking()
            .Include(t => t.Mappings)
            .FirstAsync(t => t.Id == task.Id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var e = await _db.EtlTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (e is null) return;
        var name = e.Name;
        _db.EtlTasks.Remove(e);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditCategory.Task, AuditAction.Delete,
            $"刪除任務「{name}」",
            targetType: nameof(EtlTask), targetId: id, targetName: name,
            severity: AuditSeverity.Warning, ct: ct);
    }
}
