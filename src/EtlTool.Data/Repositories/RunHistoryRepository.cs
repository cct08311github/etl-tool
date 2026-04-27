using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

public sealed partial class RunHistoryRepository : IRunHistorySink
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

    /// <summary>
    /// 分頁查詢某 task 的歷史。回傳 (items, totalCount)。
    /// 用於 /api/tasks/{id}/runs?page=N&size=M JSON endpoint。
    /// page 從 1 起算；page 或 size ≤ 0 視為非法（call site 已 clamp）。
    /// </summary>
    public async Task<(List<RunHistory> Items, int Total)> ListByTaskPagedAsync(
        Guid taskId, int page, int size, CancellationToken ct)
    {
        var q = _db.RunHistories.AsNoTracking().Where(r => r.EtlTaskId == taskId);
        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<List<RunHistory>> ListRecentAsync(int take, CancellationToken ct)
        => _db.RunHistories.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>
    /// 全域分頁 + 篩選。給 /runs 全域歷史頁面用。
    /// 篩選都是「至少要符合一個」的 OR-on-list / AND-across-fields 邏輯。
    /// 注意：errorClass 篩選需要在記憶體端跑分類（無法在 DB 過濾），所以對大量
    /// 失敗 run 的場景會把符合 status / date / taskId 的全撈進來再分類過濾。
    /// 實務上 RunHistory 已被保留政策限制，全表通常 ≤ 數萬筆，可接受。
    /// </summary>
    public async Task<(List<RunHistory> Items, int Total)> ListFilteredPagedAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        IReadOnlyCollection<RunStatus>? statuses,
        IReadOnlyCollection<Guid>? taskIds,
        string? errorClassFilter,  // null / "Transient" / "Permanent" / "Unknown"
        int page,
        int size,
        CancellationToken ct)
    {
        IQueryable<RunHistory> q = _db.RunHistories.AsNoTracking();
        if (fromUtc.HasValue) q = q.Where(r => r.StartedAt >= fromUtc.Value);
        if (toUtc.HasValue) q = q.Where(r => r.StartedAt <= toUtc.Value);
        if (statuses is { Count: > 0 }) q = q.Where(r => statuses.Contains(r.Status));
        if (taskIds is { Count: > 0 }) q = q.Where(r => taskIds.Contains(r.EtlTaskId));

        if (string.IsNullOrEmpty(errorClassFilter))
        {
            // 純 DB 路徑 — 可直接 Skip/Take
            var total = await q.CountAsync(ct);
            var items = await q
                .OrderByDescending(r => r.StartedAt)
                .Skip((page - 1) * size)
                .Take(size)
                .ToListAsync(ct);
            return (items, total);
        }

        // errorClass 過濾必須在記憶體端跑（classifier 是純 C# helper）。
        // 為避免一次拉太多，先撈 ID + Started + Status + ErrorMessage（輕量），
        // 在記憶體分類後挑出符合的，再分頁取 RunHistory entity。
        var lite = await q
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.Id, r.Status, r.ErrorMessage })
            .ToListAsync(ct);

        var filteredIds = lite
            .Where(x => string.Equals(
                ClassifyOrUnknown(x.ErrorMessage),
                errorClassFilter,
                StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToList();

        var matchingItems = await _db.RunHistories.AsNoTracking()
            .Where(r => filteredIds.Contains(r.Id))
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return (matchingItems, filteredIds.Count);
    }

    private static string ClassifyOrUnknown(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return "Unknown";
        return EngineErrorClassifier.Classify(new Exception(errorMessage)).Class.ToString();
    }

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

    /// <summary>IRunHistorySink 介面的同名方法 — 把 Dictionary 包成 IReadOnlyDictionary 回傳。</summary>
    async Task<IReadOnlyDictionary<Guid, DateTime>> IRunHistorySink.LastSuccessByTaskAsync(CancellationToken ct)
        => await LastSuccessByTaskAsync(ct);

    /// <summary>
    /// 一次撈過去 N 天每個 task 的最近 K 筆 run（給 Tasks list inline sparkline 用）。
    /// 用一個 Where + 排序的 query，在 memory 裡 group + take N 避免 N+1。
    /// 只回傳 Status / StartedAt / FinishedAt（sparkline 與 p95 計算需要的最小欄位）。
    /// </summary>
    public async Task<Dictionary<Guid, List<RunHistorySummary>>> RecentRunsByTaskAsync(
        TimeSpan window, int takePerTask, CancellationToken ct)
    {
        var since = DateTime.UtcNow.Subtract(window);
        var raw = await _db.RunHistories
            .AsNoTracking()
            .Where(r => r.StartedAt >= since && r.Status != RunStatus.Running)
            .Select(r => new RunHistorySummary(
                r.EtlTaskId, r.StartedAt, r.FinishedAt, r.Status))
            .ToListAsync(ct);

        return raw
            .GroupBy(r => r.EtlTaskId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.StartedAt).Take(takePerTask).Reverse().ToList());
    }
}

/// <summary>給 Tasks list inline sparkline / p95 計算用的精簡 record。</summary>
public sealed record RunHistorySummary(
    Guid EtlTaskId, DateTime StartedAt, DateTime? FinishedAt, RunStatus Status);

public sealed partial class RunHistoryRepository
{

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

    /// <summary>
    /// 過去 N 天每個 task 的 (success_count, total_count)。
    /// 用 GROUP BY 一次撈完，避免 N+1。total=0 的 task 不會出現在 dict 裡。
    /// 計算 SLA：success / total（顯示用，呼叫端決定 threshold）。
    /// </summary>
    public async Task<Dictionary<Guid, (int Success, int Total)>> SuccessRateByTaskAsync(
        TimeSpan window, CancellationToken ct)
    {
        var since = DateTime.UtcNow.Subtract(window);
        var grouped = await _db.RunHistories
            .AsNoTracking()
            .Where(r => r.StartedAt >= since && r.Status != RunStatus.Running)
            .GroupBy(r => r.EtlTaskId)
            .Select(g => new
            {
                TaskId = g.Key,
                Total = g.Count(),
                Success = g.Count(r => r.Status == RunStatus.Success),
            })
            .ToListAsync(ct);
        return grouped.ToDictionary(x => x.TaskId, x => (x.Success, x.Total));
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
