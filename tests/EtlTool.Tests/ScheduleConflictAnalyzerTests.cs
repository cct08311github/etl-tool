using EtlTool.Core.Models;
using EtlTool.Core.Scheduling;

namespace EtlTool.Tests;

/// <summary>
/// 給定一組 EtlTask + 模擬「現在時間」，驗 ScheduleConflictAnalyzer 的兩類偵測：
///   1. 同分鐘 + 同目標表 → SameTargetCollision
///   2. 5 分鐘內 ≥3 個任務同來源 → SourcePressure
///
/// 測試用固定 cron 表達式（毎天 02:00 / 02:01 / 02:05），確認：
/// - 不同 task / 不同 cron / 不同 target 的組合應在預期時點出現衝突
/// - 單一 task 多次 fire 不算衝突（自己跟自己不會打架）
/// </summary>
public class ScheduleConflictAnalyzerTests
{
    private static EtlTask Task(string name, string cron,
        Guid? srcConn = null, Guid? tgtConn = null,
        string targetSchema = "dbo", string targetTable = "Out") =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            CronExpression = cron,
            Enabled = true,
            SourceConnectionId = srcConn ?? Guid.Empty,
            TargetConnectionId = tgtConn ?? Guid.Empty,
            TargetSchema = targetSchema,
            TargetTable = targetTable,
        };

    // 用 2026-01-01 00:00 之前一秒當「現在」，第一次觸發就會在 02:00
    private static readonly DateTime _now = new(2025, 12, 31, 23, 59, 59);

    [Fact]
    public void No_tasks_no_conflicts()
    {
        var conflicts = ScheduleConflictAnalyzer.Analyze(Array.Empty<EtlTask>(), _now, windowHours: 24);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Single_task_never_conflicts_with_itself()
    {
        // 每分鐘觸發 → 24h 內 1440 次，但全是同一個 task → 不該報 SameTargetCollision
        var t = Task("solo", "0 * * * * ?", tgtConn: Guid.NewGuid());
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { t }, _now, windowHours: 24);
        Assert.DoesNotContain(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SameTargetCollision);
        Assert.DoesNotContain(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SourcePressure);
    }

    [Fact]
    public void Two_tasks_same_minute_same_target_flagged()
    {
        var sameTarget = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", tgtConn: sameTarget, targetTable: "Orders");
        var b = Task("B", "0 0 2 * * ?", tgtConn: sameTarget, targetTable: "Orders");
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        var collisions = conflicts.Where(c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SameTargetCollision).ToList();
        Assert.NotEmpty(collisions);
        Assert.Contains("A", collisions[0].TaskNames);
        Assert.Contains("B", collisions[0].TaskNames);
    }

    [Fact]
    public void Different_tables_same_minute_no_collision()
    {
        var sameTarget = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", tgtConn: sameTarget, targetTable: "Orders");
        var b = Task("B", "0 0 2 * * ?", tgtConn: sameTarget, targetTable: "Customers");
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        Assert.DoesNotContain(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SameTargetCollision);
    }

    [Fact]
    public void Different_minutes_no_collision()
    {
        var sameTarget = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", tgtConn: sameTarget, targetTable: "Orders");
        var b = Task("B", "0 5 2 * * ?", tgtConn: sameTarget, targetTable: "Orders");  // 02:05
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        Assert.DoesNotContain(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SameTargetCollision);
    }

    [Fact]
    public void Three_tasks_same_source_within_5min_pressure_warning()
    {
        var src = Guid.NewGuid();
        // 02:00, 02:01, 02:04 — 全在 5 分鐘窗口內
        var a = Task("A", "0 0 2 * * ?", srcConn: src, targetTable: "T1");
        var b = Task("B", "0 1 2 * * ?", srcConn: src, targetTable: "T2");
        var c = Task("C", "0 4 2 * * ?", srcConn: src, targetTable: "T3");
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b, c }, _now, windowHours: 24);
        var pressures = conflicts.Where(x => x.Kind == ScheduleConflictAnalyzer.ConflictKind.SourcePressure).ToList();
        Assert.NotEmpty(pressures);
        Assert.Equal(3, pressures[0].TaskNames.Count);
    }

    [Fact]
    public void Two_tasks_same_source_no_pressure()
    {
        // 門檻是 ≥3，2 個不該觸發
        var src = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", srcConn: src);
        var b = Task("B", "0 1 2 * * ?", srcConn: src);
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        Assert.DoesNotContain(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SourcePressure);
    }

    [Fact]
    public void Disabled_task_excluded()
    {
        var sameTarget = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", tgtConn: sameTarget);
        var b = Task("B", "0 0 2 * * ?", tgtConn: sameTarget);
        b.Enabled = false;
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        Assert.DoesNotContain(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SameTargetCollision);
    }

    [Fact]
    public void Bad_cron_silently_skipped()
    {
        var sameTarget = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", tgtConn: sameTarget);
        var b = Task("BAD", "this is not a cron expression", tgtConn: sameTarget);
        // 不該丟例外，只是 BAD 不會被算進來
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        Assert.DoesNotContain(conflicts, c => c.TaskNames.Contains("BAD"));
    }

    [Fact]
    public void Schema_case_normalized_when_comparing_targets()
    {
        // Oracle 通常回大寫；UI 可能存小寫；衝突偵測應忽略大小寫
        var sameTarget = Guid.NewGuid();
        var a = Task("A", "0 0 2 * * ?", tgtConn: sameTarget, targetSchema: "HR", targetTable: "EMP");
        var b = Task("B", "0 0 2 * * ?", tgtConn: sameTarget, targetSchema: "hr", targetTable: "emp");
        var conflicts = ScheduleConflictAnalyzer.Analyze(new[] { a, b }, _now, windowHours: 24);
        Assert.Contains(conflicts, c => c.Kind == ScheduleConflictAnalyzer.ConflictKind.SameTargetCollision);
    }
}
