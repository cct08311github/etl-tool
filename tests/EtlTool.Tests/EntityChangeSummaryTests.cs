using EtlTool.Core.Models;
using EtlTool.Data.Repositories;

namespace EtlTool.Tests;

public class EntityChangeSummaryTests
{
    private record Sample(string Name, bool Enabled, int BatchSize, string? Note = null);

    [Fact]
    public void Created_action_returns_marker()
    {
        var summary = EntityChangeHistoryRepository.ComputeSummary(
            null, new Sample("a", true, 100), EntityChangeAction.Created);
        Assert.Equal("(新建)", summary);
    }

    [Fact]
    public void Deleted_action_returns_marker()
    {
        var summary = EntityChangeHistoryRepository.ComputeSummary(
            new Sample("a", true, 100), null, EntityChangeAction.Deleted);
        Assert.Equal("(刪除)", summary);
    }

    [Fact]
    public void Updated_with_no_changes_says_so()
    {
        var same = new Sample("a", true, 100);
        var copy = new Sample("a", true, 100);
        var summary = EntityChangeHistoryRepository.ComputeSummary(same, copy, EntityChangeAction.Updated);
        Assert.Contains("無欄位差異", summary!);
    }

    [Fact]
    public void Updated_with_single_field_change()
    {
        var before = new Sample("a", true, 100);
        var after = new Sample("a", false, 100);
        var summary = EntityChangeHistoryRepository.ComputeSummary(before, after, EntityChangeAction.Updated);
        Assert.Contains("Enabled", summary!);
        Assert.Contains("true", summary);
        Assert.Contains("false", summary);
        Assert.Contains("→", summary);
    }

    [Fact]
    public void Updated_with_multiple_field_changes_separated_by_semicolon()
    {
        var before = new Sample("a", true, 100);
        var after = new Sample("b", false, 200);
        var summary = EntityChangeHistoryRepository.ComputeSummary(before, after, EntityChangeAction.Updated);
        Assert.Contains("Name", summary!);
        Assert.Contains("Enabled", summary);
        Assert.Contains("BatchSize", summary);
        // 多筆變更應該有 "; " 分隔
        Assert.True(summary.Contains("; "));
    }

    [Fact]
    public void Null_before_or_after_in_updated_returns_null()
    {
        Assert.Null(EntityChangeHistoryRepository.ComputeSummary(null, new Sample("a", true, 100), EntityChangeAction.Updated));
        Assert.Null(EntityChangeHistoryRepository.ComputeSummary(new Sample("a", true, 100), null, EntityChangeAction.Updated));
    }

    [Fact]
    public void Optional_string_field_change_to_null()
    {
        var before = new Sample("a", true, 100, Note: "hello");
        var after = new Sample("a", true, 100, Note: null);
        var summary = EntityChangeHistoryRepository.ComputeSummary(before, after, EntityChangeAction.Updated);
        Assert.Contains("Note", summary!);
        Assert.Contains("hello", summary);
        Assert.Contains("<null>", summary);
    }

    [Fact]
    public void Optional_string_field_change_from_null()
    {
        var before = new Sample("a", true, 100, Note: null);
        var after = new Sample("a", true, 100, Note: "world");
        var summary = EntityChangeHistoryRepository.ComputeSummary(before, after, EntityChangeAction.Updated);
        Assert.Contains("Note", summary!);
        Assert.Contains("<null>", summary);
        Assert.Contains("world", summary);
    }

    [Fact]
    public void Long_string_value_truncated_in_render()
    {
        // 超過 60 字應加 "…"
        var before = new Sample("a", true, 100, Note: new string('x', 100));
        var after = new Sample("a", true, 100, Note: "short");
        var summary = EntityChangeHistoryRepository.ComputeSummary(before, after, EntityChangeAction.Updated);
        Assert.Contains("…", summary!);
    }

    [Fact]
    public void Auth_options_session_timeout_30_min_default()
    {
        var opts = new EtlTool.App.Auth.AuthOptions();
        // 預設應該是 30 分鐘（銀行內網標準）
        Assert.Equal(30, opts.ResolveTimeoutMinutes());
    }

    [Fact]
    public void Auth_options_session_timeout_explicit_value()
    {
        var opts = new EtlTool.App.Auth.AuthOptions { SessionTimeoutMinutes = 60 };
        Assert.Equal(60, opts.ResolveTimeoutMinutes());
    }

    [Fact]
    public void Auth_options_session_timeout_capped_at_8_hours()
    {
        var opts = new EtlTool.App.Auth.AuthOptions { SessionTimeoutMinutes = 9999 };
        Assert.Equal(480, opts.ResolveTimeoutMinutes());
    }

    [Fact]
    public void Auth_options_back_compat_session_hours()
    {
        // 舊欄位 SessionHours 仍應被讀取
        var opts = new EtlTool.App.Auth.AuthOptions { SessionTimeoutMinutes = 0, SessionHours = 4 };
        Assert.Equal(240, opts.ResolveTimeoutMinutes());
    }

    [Fact]
    public void Auth_options_new_field_takes_precedence_over_legacy()
    {
        var opts = new EtlTool.App.Auth.AuthOptions { SessionTimeoutMinutes = 15, SessionHours = 8 };
        Assert.Equal(15, opts.ResolveTimeoutMinutes());
    }
}
