using EtlTool.Core.Models;

namespace EtlTool.Core.Scheduling;

/// <summary>
/// 在指定時窗（預設 24 小時）內預演所有 enabled 任務的 cron 觸發點，
/// 並偵測「會打架」的兩種模式：
///
///   1. <b>SameTargetCollision</b>：≥2 個任務在同一分鐘觸發，且寫入同一個目標表
///      （target_connection_id + target_schema + target_table 完全相同）
///      → race condition 風險：兩個 transaction 可能互相 deadlock 或彼此覆寫
///
///   2. <b>SourcePressure</b>：≥3 個任務在 5 分鐘窗口內觸發，且都從同一個來源連線拉
///      → 連線池壓力 / 來源 DB CPU 壓力警告（不一定是 bug，但運維會想知道）
///
/// 純查表 + cron walk，不打 DB；同樣不依賴 Quartz scheduler 的 in-memory 狀態
/// （只看 EtlTask.CronExpression）。給 TaskEdit 存檔流程 + /scheduler 頁顯示用。
///
/// 設計成 static + 純 input/output，方便寫單元測試（給定一組 task → 預期 conflict 集合）。
/// </summary>
public static class ScheduleConflictAnalyzer
{
    public enum ConflictKind
    {
        /// <summary>同分鐘 + 同目標表 → race condition</summary>
        SameTargetCollision,
        /// <summary>5 分鐘內 ≥3 個任務從同一來源連線拉 → 來源壓力</summary>
        SourcePressure,
    }

    public sealed record Conflict(
        ConflictKind Kind,
        DateTime At,            // local time
        IReadOnlyList<Guid> TaskIds,
        IReadOnlyList<string> TaskNames,
        string Description);

    private sealed record FireEntry(Guid TaskId, string TaskName, DateTime At, EtlTask Task);

    /// <summary>
    /// 偵測時窗內的所有衝突。<paramref name="windowHours"/> 預設 24 小時，
    /// <paramref name="maxFiresPerTask"/> 預設 50（防止 every-minute cron 算到爆）。
    /// </summary>
    public static IReadOnlyList<Conflict> Analyze(
        IReadOnlyList<EtlTask> tasks,
        DateTime nowLocal,
        int windowHours = 24,
        int maxFiresPerTask = 50)
    {
        var endLocal = nowLocal.AddHours(windowHours);
        var fires = new List<FireEntry>();
        foreach (var t in tasks)
        {
            if (!t.Enabled) continue;
            if (string.IsNullOrWhiteSpace(t.CronExpression)) continue;

            Quartz.CronExpression expr;
            try { expr = new Quartz.CronExpression(t.CronExpression); }
            catch { continue; }  // 壞 cron 不算進衝突分析（其他地方會警告）

            var pointer = new DateTimeOffset(nowLocal);
            for (int i = 0; i < maxFiresPerTask; i++)
            {
                var nextOpt = expr.GetNextValidTimeAfter(pointer);
                if (nextOpt is null) break;
                var next = nextOpt.Value.LocalDateTime;
                if (next > endLocal) break;
                fires.Add(new FireEntry(t.Id, t.Name, next, t));
                pointer = nextOpt.Value;
            }
        }

        var conflicts = new List<Conflict>();
        conflicts.AddRange(DetectSameTargetCollisions(fires));
        conflicts.AddRange(DetectSourcePressure(fires));
        return conflicts.OrderBy(c => c.At).ThenBy(c => c.Kind).ToList();
    }

    private static IEnumerable<Conflict> DetectSameTargetCollisions(IReadOnlyList<FireEntry> fires)
    {
        // 把 fires 依「同分鐘 + 同目標表」分組
        // 鍵：(yyyyMMddHHmm, target_conn, lower(target_schema), lower(target_table))
        // 用 string 接住空 schema（Oracle 預設用空字串）
        var groups = fires
            .GroupBy(f => (
                Minute: TruncateToMinute(f.At),
                Conn: f.Task.TargetConnectionId,
                Schema: (f.Task.TargetSchema ?? "").ToLowerInvariant(),
                Table: (f.Task.TargetTable ?? "").ToLowerInvariant()
            ));

        foreach (var g in groups)
        {
            // 不同 task 才算衝突；同一 task 多次觸發不重複報
            var distinctTasks = g.Select(f => f.TaskId).Distinct().ToList();
            if (distinctTasks.Count < 2) continue;

            var names = g.Select(f => f.TaskName).Distinct().ToList();
            var sample = g.First().Task;
            var schemaTable = string.IsNullOrEmpty(sample.TargetSchema)
                ? sample.TargetTable
                : $"{sample.TargetSchema}.{sample.TargetTable}";
            yield return new Conflict(
                Kind: ConflictKind.SameTargetCollision,
                At: g.Key.Minute,
                TaskIds: distinctTasks,
                TaskNames: names,
                Description: $"{names.Count} 個任務在 {g.Key.Minute:HH:mm} 同時寫入 {schemaTable}：{string.Join("、", names)}。" +
                             "Delete-Insert + Upsert 同時跑可能 deadlock 或彼此覆寫；建議錯開分鐘或合併成單一任務。");
        }
    }

    private static IEnumerable<Conflict> DetectSourcePressure(IReadOnlyList<FireEntry> fires)
    {
        // 5 分鐘 sliding window：對每筆 fire 看「同來源連線 + ±5 分」內共多少筆
        // 為避免報太多重複，每個 5 分鐘 bucket 只報一次（用 bucket 起始點當 key）
        var reportedBuckets = new HashSet<(DateTime BucketStart, Guid SourceConn)>();
        var bySource = fires.GroupBy(f => f.Task.SourceConnectionId);
        foreach (var sg in bySource)
        {
            var ordered = sg.OrderBy(f => f.At).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var anchor = ordered[i];
                var windowEnd = anchor.At.AddMinutes(5);
                var inWindow = ordered.Skip(i).TakeWhile(f => f.At <= windowEnd).ToList();
                var distinctTasks = inWindow.Select(f => f.TaskId).Distinct().ToList();
                if (distinctTasks.Count < 3) continue;

                // bucket = anchor 的「以 5 分鐘為單位」起點，避免同一壓力期重複報
                var bucket = new DateTime(anchor.At.Year, anchor.At.Month, anchor.At.Day,
                    anchor.At.Hour, anchor.At.Minute - (anchor.At.Minute % 5), 0);
                if (!reportedBuckets.Add((bucket, sg.Key))) continue;

                var names = inWindow.Select(f => f.TaskName).Distinct().ToList();
                yield return new Conflict(
                    Kind: ConflictKind.SourcePressure,
                    At: anchor.At,
                    TaskIds: distinctTasks,
                    TaskNames: names,
                    Description: $"{names.Count} 個任務在 {anchor.At:HH:mm} 起 5 分鐘內從同一來源連線拉資料：" +
                                 $"{string.Join("、", names)}。可能造成連線池 / 來源 DB CPU 壓力，建議錯開或開新連線分流。");
            }
        }
    }

    private static DateTime TruncateToMinute(DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);

    public static string KindLabel(ConflictKind k) => k switch
    {
        ConflictKind.SameTargetCollision => "同表寫入衝突",
        ConflictKind.SourcePressure => "來源連線壓力",
        _ => k.ToString(),
    };
}
