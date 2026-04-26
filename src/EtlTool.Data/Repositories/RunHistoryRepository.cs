using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed class RunHistoryRepository : IRunHistorySink
{
    /// <summary>每個 task 保留最近 N 筆，超出由背景清除。</summary>
    public const int RetainPerTask = 100;

    private readonly AppDbContext _db;
    public RunHistoryRepository(AppDbContext db) { _db = db; }

    public async Task PersistAsync(RunHistory run, CancellationToken ct)
    {
        var tracked = await _db.RunHistories.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
        if (tracked is null)
        {
            _db.RunHistories.Add(run);
        }
        else
        {
            tracked.Status = run.Status;
            tracked.FinishedAt = run.FinishedAt;
            tracked.RowsRead = run.RowsRead;
            tracked.RowsWritten = run.RowsWritten;
            tracked.GeneratedReadSql = run.GeneratedReadSql;
            tracked.GeneratedWriteSql = run.GeneratedWriteSql;
            tracked.SamplePayloadJson = run.SamplePayloadJson;
            tracked.ErrorMessage = run.ErrorMessage;
            tracked.TriggerType = run.TriggerType;
        }
        await _db.SaveChangesAsync(ct);
    }

    public Task<RunHistory?> GetAsync(Guid id, CancellationToken ct)
        => _db.RunHistories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<List<RunHistory>> ListByTaskAsync(Guid taskId, int take, CancellationToken ct)
        => _db.RunHistories.AsNoTracking()
            .Where(r => r.EtlTaskId == taskId)
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<List<RunHistory>> ListRecentAsync(int take, CancellationToken ct)
        => _db.RunHistories.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>清理單一 task 超出保留筆數的舊紀錄。</summary>
    public async Task PurgeOldAsync(Guid taskId, CancellationToken ct)
    {
        var ids = await _db.RunHistories
            .Where(r => r.EtlTaskId == taskId)
            .OrderByDescending(r => r.StartedAt)
            .Skip(RetainPerTask)
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (ids.Count == 0) return;

        await _db.RunHistories
            .Where(r => ids.Contains(r.Id))
            .ExecuteDeleteAsync(ct);
    }
}
