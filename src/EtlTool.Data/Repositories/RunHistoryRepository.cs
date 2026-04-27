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

    /// <summary>
    /// 一次撈所有 task 的最近一次 Success run 開始時間。
    /// 用單一 query (group by) 避免 N+1。
    /// 沒成功過的 task 不會在 dict 裡。
    /// </summary>
    public async Task<Dictionary<Guid, DateTime>> LastSuccessByTaskAsync(CancellationToken ct)
    {
        var grouped = await _db.RunHistories
            .AsNoTracking()
            .Where(r => r.Status == RunStatus.Success)
            .GroupBy(r => r.EtlTaskId)
            .Select(g => new { TaskId = g.Key, LastAt = g.Max(r => r.StartedAt) })
            .ToListAsync(ct);
        return grouped.ToDictionary(x => x.TaskId, x => x.LastAt);
    }

    /// <summary>
    /// 同 LastSuccessByTaskAsync 但抓 Failed runs。給 Tasks list 顯示「上次失敗」用。
    /// 沒失敗過的 task 不會在 dict 裡。
    /// </summary>
    public async Task<Dictionary<Guid, DateTime>> LastFailureByTaskAsync(CancellationToken ct)
    {
        var grouped = await _db.RunHistories
            .AsNoTracking()
            .Where(r => r.Status == RunStatus.Failed)
            .GroupBy(r => r.EtlTaskId)
            .Select(g => new { TaskId = g.Key, LastAt = g.Max(r => r.StartedAt) })
            .ToListAsync(ct);
        return grouped.ToDictionary(x => x.TaskId, x => x.LastAt);
    }

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
