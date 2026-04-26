using EtlTool.Core.Models;

namespace EtlTool.App.Services;

/// <summary>
/// 把 EtlTask + RunHistory 組成不同 webhook 平台對應的 JSON payload。
/// 純函式，方便 TDD（不涉及 HttpClient）。
///
/// 支援格式：
///   - "slack"   — Slack incoming-webhook 格式（attachments + color + fields）
///   - "teams"   — Microsoft Teams MessageCard 格式
///   - "generic" — 平鋪 JSON 物件（既有 HttpFailureNotifier 行為，向後相容）
/// </summary>
public static class WebhookPayloadBuilder
{
    public enum Format { Generic, Slack, Teams }

    public static Format ParseFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Format.Generic;
        return raw.Trim().ToLowerInvariant() switch
        {
            "slack" => Format.Slack,
            "teams" => Format.Teams,
            "msteams" => Format.Teams,
            _ => Format.Generic,
        };
    }

    public static object BuildPayload(Format format, EtlTask task, RunHistory run, string? error)
    {
        var isRecovery = error?.StartsWith("[RECOVERY", StringComparison.Ordinal) == true;
        var isStreak = error?.StartsWith("[STREAK", StringComparison.Ordinal) == true;
        var isSuccess = run.Status == RunStatus.Success;

        return format switch
        {
            Format.Slack => BuildSlack(task, run, error, isRecovery, isStreak, isSuccess),
            Format.Teams => BuildTeams(task, run, error, isRecovery, isStreak, isSuccess),
            _ => BuildGeneric(task, run, error),
        };
    }

    private static object BuildGeneric(EtlTask task, RunHistory run, string? error) => new
    {
        text = $"⚠ ETL 任務狀態：「{task.Name}」({run.Status})",
        task_id = task.Id.ToString(),
        task_name = task.Name,
        run_id = run.Id.ToString(),
        status = run.Status.ToString(),
        trigger_type = run.TriggerType.ToString(),
        started_at = run.StartedAt.ToString("o"),
        finished_at = run.FinishedAt?.ToString("o"),
        rows_read = run.RowsRead,
        rows_written = run.RowsWritten,
        error = Truncate(error, 1000),
    };

    private static object BuildSlack(EtlTask task, RunHistory run, string? error,
        bool isRecovery, bool isStreak, bool isSuccess)
    {
        var color = isRecovery ? "good" : (isSuccess ? "good" : "danger");
        var emoji = isRecovery ? "✅" : (isSuccess ? "✓" : (isStreak ? "🚨" : "⚠"));
        var headline = isRecovery
            ? $"{emoji} ETL 任務恢復：「{task.Name}」"
            : isStreak
                ? $"{emoji} ETL 任務連續失敗：「{task.Name}」"
                : isSuccess
                    ? $"{emoji} ETL 任務成功：「{task.Name}」"
                    : $"{emoji} ETL 任務失敗：「{task.Name}」";

        var fields = new List<object>
        {
            new { title = "任務", value = task.Name, @short = true },
            new { title = "狀態", value = run.Status.ToString(), @short = true },
            new { title = "觸發", value = run.TriggerType.ToString(), @short = true },
            new { title = "讀取/寫入", value = $"{run.RowsRead} / {run.RowsWritten}", @short = true },
            new { title = "開始", value = run.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC", @short = true },
        };
        if (run.FinishedAt is not null)
        {
            fields.Add(new { title = "結束", value = run.FinishedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") + " UTC", @short = true });
        }
        if (!string.IsNullOrEmpty(error))
        {
            fields.Add(new { title = "錯誤", value = Truncate(error, 500) ?? "", @short = false });
        }

        var ts = run.StartedAt.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        return new
        {
            text = headline,
            attachments = new[]
            {
                new
                {
                    color,
                    title = task.Name,
                    fields = fields.ToArray(),
                    footer = "EtlTool",
                    ts = (long)ts,
                }
            }
        };
    }

    private static object BuildTeams(EtlTask task, RunHistory run, string? error,
        bool isRecovery, bool isStreak, bool isSuccess)
    {
        // Teams legacy MessageCard format。Adaptive Card 較複雜，不在此版本支援。
        var themeColor = isRecovery ? "00B36B"  // green
            : (isSuccess ? "00B36B"
                : (isStreak ? "C5221F" /* red */ : "F2A020" /* amber */));
        var summary = isRecovery
            ? $"ETL 任務恢復：{task.Name}"
            : isStreak
                ? $"ETL 任務連續失敗：{task.Name}"
                : isSuccess
                    ? $"ETL 任務成功：{task.Name}"
                    : $"ETL 任務失敗：{task.Name}";

        var facts = new List<object>
        {
            new { name = "任務",       value = task.Name },
            new { name = "狀態",       value = run.Status.ToString() },
            new { name = "觸發",       value = run.TriggerType.ToString() },
            new { name = "讀取 / 寫入", value = $"{run.RowsRead} / {run.RowsWritten}" },
            new { name = "開始 (UTC)", value = run.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") },
        };
        if (run.FinishedAt is not null)
            facts.Add(new { name = "結束 (UTC)", value = run.FinishedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") });
        if (!string.IsNullOrEmpty(error))
            facts.Add(new { name = "錯誤", value = Truncate(error, 500) ?? "" });

        return new
        {
            type = "MessageCard",
            context = "https://schema.org/extensions",
            themeColor,
            summary,
            title = summary,
            sections = new[]
            {
                new
                {
                    activityTitle = task.Name,
                    activitySubtitle = $"Run ID: {run.Id}",
                    facts = facts.ToArray(),
                    markdown = true,
                }
            }
        };
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max] + "… (truncated)";
    }
}
