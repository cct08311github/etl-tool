using System.Text.Json;
using EtlTool.App.Services;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class WebhookPayloadBuilderTests
{
    [Theory]
    [InlineData(null, WebhookPayloadBuilder.Format.Generic)]
    [InlineData("", WebhookPayloadBuilder.Format.Generic)]
    [InlineData(" ", WebhookPayloadBuilder.Format.Generic)]
    [InlineData("generic", WebhookPayloadBuilder.Format.Generic)]
    [InlineData("Slack", WebhookPayloadBuilder.Format.Slack)]
    [InlineData("SLACK", WebhookPayloadBuilder.Format.Slack)]
    [InlineData("teams", WebhookPayloadBuilder.Format.Teams)]
    [InlineData("MsTeams", WebhookPayloadBuilder.Format.Teams)]
    [InlineData("garbage", WebhookPayloadBuilder.Format.Generic)]
    public void ParseFormat_recognises_known_values(string? raw, WebhookPayloadBuilder.Format expected)
    {
        Assert.Equal(expected, WebhookPayloadBuilder.ParseFormat(raw));
    }

    private static (EtlTask t, RunHistory r) MakeFailedRun(string? error = "boom")
    {
        var task = new EtlTask { Id = Guid.NewGuid(), Name = "MyTask" };
        var run = new RunHistory
        {
            Id = Guid.NewGuid(),
            EtlTaskId = task.Id,
            Status = RunStatus.Failed,
            StartedAt = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 4, 27, 10, 5, 0, DateTimeKind.Utc),
            RowsRead = 100,
            RowsWritten = 0,
            TriggerType = TriggerType.Scheduled,
            ErrorMessage = error,
        };
        return (task, run);
    }

    private static (EtlTask t, RunHistory r) MakeSuccessRun(string? error = null, long rowsWritten = 95)
    {
        var task = new EtlTask { Id = Guid.NewGuid(), Name = "MyTask" };
        var run = new RunHistory
        {
            Id = Guid.NewGuid(),
            EtlTaskId = task.Id,
            Status = RunStatus.Success,
            StartedAt = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 4, 27, 10, 1, 0, DateTimeKind.Utc),
            RowsRead = 100,
            RowsWritten = rowsWritten,
            TriggerType = TriggerType.Scheduled,
            ErrorMessage = error,
        };
        return (task, run);
    }

    private static string SerializeToJson(object payload)
        => JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            // 保留中文 + emoji 原樣，方便 assertion 用人類可讀字串。
            // 實務上 Slack / Teams 也接受 \uXXXX 形式，這純粹是測試方便。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    [Fact]
    public void Generic_failure_payload_has_expected_fields()
    {
        var (t, r) = MakeFailedRun();
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Generic, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        Assert.Contains("MyTask", json);
        Assert.Contains("Failed", json);
        Assert.Contains("Scheduled", json);
        Assert.Contains("\"rows_read\":100", json);
        Assert.Contains("boom", json);
    }

    [Fact]
    public void Slack_failure_uses_danger_color_and_warning_emoji()
    {
        var (t, r) = MakeFailedRun();
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Slack, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        Assert.Contains("\"color\":\"danger\"", json);
        Assert.Contains("⚠", json);
        Assert.Contains("attachments", json);
        Assert.Contains("\"footer\":\"EtlTool\"", json);
    }

    [Fact]
    public void Slack_streak_failure_marks_consecutive_failure()
    {
        var (t, r) = MakeFailedRun(error: "[STREAK 3/3] real boom");
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Slack, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        // Header should clearly say it's a consecutive-failure escalation
        Assert.Contains("連續失敗", json);
        Assert.Contains("\"color\":\"danger\"", json);
        // STREAK marker preserved in the error field so on-call sees the count
        Assert.Contains("STREAK 3/3", json);
    }

    [Fact]
    public void Slack_recovery_uses_good_color_and_check_emoji()
    {
        var (t, r) = MakeSuccessRun(error: "[RECOVERY after 3 consecutive failures]");
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Slack, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        Assert.Contains("✅", json);
        Assert.Contains("\"color\":\"good\"", json);
        Assert.Contains("恢復", json);
    }

    [Fact]
    public void Slack_plain_success_uses_good_color()
    {
        var (t, r) = MakeSuccessRun();
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Slack, t, r, null);
        var json = SerializeToJson(p);
        Assert.Contains("\"color\":\"good\"", json);
        Assert.Contains("成功", json);
    }

    [Fact]
    public void Teams_failure_uses_amber_themeColor_and_MessageCard_type()
    {
        var (t, r) = MakeFailedRun();
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Teams, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        Assert.Contains("\"type\":\"MessageCard\"", json);
        Assert.Contains("F2A020", json);  // amber for non-streak fail
        Assert.Contains("MyTask", json);
        Assert.Contains("sections", json);
    }

    [Fact]
    public void Teams_streak_failure_uses_red_themeColor()
    {
        var (t, r) = MakeFailedRun(error: "[STREAK 3/3] still broken");
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Teams, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        Assert.Contains("C5221F", json);  // red
    }

    [Fact]
    public void Teams_recovery_uses_green_themeColor()
    {
        var (t, r) = MakeSuccessRun(error: "[RECOVERY after 5 consecutive failures]");
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Teams, t, r, r.ErrorMessage);
        var json = SerializeToJson(p);
        Assert.Contains("00B36B", json);  // green
        Assert.Contains("恢復", json);
    }

    [Fact]
    public void Long_error_message_truncated()
    {
        var longErr = new string('x', 2000);
        var (t, r) = MakeFailedRun(error: longErr);
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Generic, t, r, longErr);
        var json = SerializeToJson(p);
        Assert.Contains("truncated", json);
        // Generic 限 1000 chars
        Assert.True(json.Length < longErr.Length + 500);
    }

    [Fact]
    public void Slack_no_error_message_omits_error_field()
    {
        var (t, r) = MakeSuccessRun();
        var p = WebhookPayloadBuilder.BuildPayload(WebhookPayloadBuilder.Format.Slack, t, r, null);
        var json = SerializeToJson(p);
        // 不應該有「錯誤」這個 field（只有 error message 時才會 add）
        Assert.DoesNotContain("\"title\":\"錯誤\"", json);
    }
}
