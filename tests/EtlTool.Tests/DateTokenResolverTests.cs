using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class DateTokenResolverTests
{
    // 2026-04-26 is Sunday; this is the time mid-April Q2.
    private static readonly DateTime Ref = new(2026, 4, 26, 14, 35, 7);

    private static DateTime AsDate(object? o) => Assert.IsType<DateTime>(o);
    private static string AsString(object? o) => Assert.IsType<string>(o);

    // === 名稱類 ===
    [Theory]
    [InlineData("${TODAY}",          2026, 4, 26, 0,  0,  0, 0)]
    [InlineData("${YESTERDAY}",      2026, 4, 25, 0,  0,  0, 0)]
    [InlineData("${TOMORROW}",       2026, 4, 27, 0,  0,  0, 0)]
    [InlineData("${NOW}",            2026, 4, 26, 14, 35, 7, 0)]
    [InlineData("${HOUR_START}",     2026, 4, 26, 14, 0,  0, 0)]
    [InlineData("${MONTH_START}",    2026, 4, 1,  0,  0,  0, 0)]
    [InlineData("${YEAR_START}",     2026, 1, 1,  0,  0,  0, 0)]
    public void Named_tokens_resolve(string token, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        var got = AsDate(DateTokenResolver.TryResolve(token, Ref));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), got);
    }

    // === 期間結尾類（END = 下期間第一刻 - 1ms） ===
    [Fact] public void Today_end()      => Assert.Equal(new DateTime(2026, 4, 26, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${TODAY_END}", Ref)));
    [Fact] public void Yesterday_end()  => Assert.Equal(new DateTime(2026, 4, 25, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${YESTERDAY_END}", Ref)));
    [Fact] public void Hour_end()       => Assert.Equal(new DateTime(2026, 4, 26, 14, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${HOUR_END}", Ref)));
    [Fact] public void Week_end()       => Assert.Equal(new DateTime(2026, 4, 26, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${WEEK_END}", Ref)));   // Sun = end of week
    [Fact] public void Month_end()      => Assert.Equal(new DateTime(2026, 4, 30, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${MONTH_END}", Ref)));
    [Fact] public void Year_end()       => Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${YEAR_END}", Ref)));

    // === 上週 / 上月 / 上年 ===
    [Fact] public void Last_week_start() => Assert.Equal(new DateTime(2026, 4, 13), AsDate(DateTokenResolver.TryResolve("${LAST_WEEK_START}", Ref)));
    [Fact] public void Last_week_end()   => Assert.Equal(new DateTime(2026, 4, 19, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${LAST_WEEK_END}", Ref)));
    [Fact] public void Last_month_start() => Assert.Equal(new DateTime(2026, 3, 1), AsDate(DateTokenResolver.TryResolve("${LAST_MONTH_START}", Ref)));
    [Fact] public void Last_month_end()   => Assert.Equal(new DateTime(2026, 3, 31, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${LAST_MONTH_END}", Ref)));
    [Fact] public void Last_year_start()  => Assert.Equal(new DateTime(2025, 1, 1), AsDate(DateTokenResolver.TryResolve("${LAST_YEAR_START}", Ref)));
    [Fact] public void Last_year_end()    => Assert.Equal(new DateTime(2025, 12, 31, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${LAST_YEAR_END}", Ref)));

    // === 季度（2026/4/26 = Q2） ===
    [Fact] public void Quarter_start()      => Assert.Equal(new DateTime(2026, 4, 1), AsDate(DateTokenResolver.TryResolve("${QUARTER_START}", Ref)));
    [Fact] public void Quarter_end()        => Assert.Equal(new DateTime(2026, 6, 30, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${QUARTER_END}", Ref)));
    [Fact] public void Last_quarter_start() => Assert.Equal(new DateTime(2026, 1, 1), AsDate(DateTokenResolver.TryResolve("${LAST_QUARTER_START}", Ref)));
    [Fact] public void Last_quarter_end()   => Assert.Equal(new DateTime(2026, 3, 31, 23, 59, 59, 999), AsDate(DateTokenResolver.TryResolve("${LAST_QUARTER_END}", Ref)));

    [Fact]
    public void WeekStart_resolves_to_monday()
    {
        Assert.Equal(new DateTime(2026, 4, 20), AsDate(DateTokenResolver.TryResolve("${WEEK_START}", Ref)));
    }

    // === 相對偏移 ===
    [Theory]
    [InlineData("${MINUTES_AGO_15}",  0, 0, -15, 0, 0)]
    [InlineData("${HOURS_AGO_3}",     0, -3, 0, 0, 0)]
    [InlineData("${DAYS_AGO_7}",     -7, 0, 0, 0, 0)]
    [InlineData("${WEEKS_AGO_2}",   -14, 0, 0, 0, 0)]
    public void Relative_offsets(string token, int days, int hours, int minutes, int _, int __)
    {
        var got = AsDate(DateTokenResolver.TryResolve(token, Ref));

        // weeks/days are date-only (no time-of-day)
        if (token.Contains("DAYS_AGO") || token.Contains("WEEKS_AGO"))
            Assert.Equal(Ref.Date.AddDays(days), got);
        else
            Assert.Equal(Ref.AddHours(hours).AddMinutes(minutes), got);
    }

    [Fact]
    public void Months_ago_handles_month_arithmetic()
    {
        // 2026-04-26 - 14 months = 2025-02-26
        var got = AsDate(DateTokenResolver.TryResolve("${MONTHS_AGO_14}", Ref));
        Assert.Equal(new DateTime(2025, 2, 26), got);
    }

    [Fact]
    public void Years_ago_5()
    {
        var got = AsDate(DateTokenResolver.TryResolve("${YEARS_AGO_5}", Ref));
        Assert.Equal(new DateTime(2021, 4, 26), got);
    }

    // === 格式化字串 ===
    [Theory]
    // 三種最常用的日期字串格式
    [InlineData("${TODAY:yyyyMMdd}",        "20260426")]      // 緊湊式（無分隔符，常見於 DW DateKey）
    [InlineData("${TODAY:yyyy-MM-dd}",      "2026-04-26")]    // ISO 8601（dash）
    [InlineData("${TODAY:yyyy/MM/dd}",      "2026/04/26")]    // 斜線
    [InlineData("${YESTERDAY:yyyy-MM-dd}",  "2026-04-25")]
    [InlineData("${YESTERDAY:yyyy/MM/dd}",  "2026/04/25")]
    [InlineData("${YESTERDAY:yyyyMMdd}",    "20260425")]
    // 月份 / 季別字串
    [InlineData("${MONTH_START:yyyyMM}",         "202604")]
    [InlineData("${MONTH_START:yyyy-MM}",        "2026-04")]
    [InlineData("${LAST_QUARTER_END:yyyy/MM/dd}", "2026/03/31")]
    // 含時分秒
    [InlineData("${NOW:yyyy-MM-dd HH:mm:ss}",     "2026-04-26 14:35:07")]
    [InlineData("${NOW:yyyyMMddHHmmss}",          "20260426143507")]
    public void Format_suffix_yields_string(string token, string expected)
    {
        var got = AsString(DateTokenResolver.TryResolve(token, Ref));
        Assert.Equal(expected, got);
    }

    [Theory]
    // 確認 raw SQL 替換時，格式化字串會自動加單引號
    [InlineData(DbProviderType.SqlServer, "${TODAY:yyyy-MM-dd}", "'2026-04-26'")]
    [InlineData(DbProviderType.SqlServer, "${TODAY:yyyy/MM/dd}", "'2026/04/26'")]
    [InlineData(DbProviderType.SqlServer, "${TODAY:yyyyMMdd}",   "'20260426'")]
    [InlineData(DbProviderType.Oracle,    "${TODAY:yyyy-MM-dd}", "'2026-04-26'")]
    [InlineData(DbProviderType.Oracle,    "${TODAY:yyyy/MM/dd}", "'2026/04/26'")]
    public void Substitute_raw_format_quotes_string(DbProviderType provider, string token, string expected)
    {
        var got = DateTokenResolver.SubstituteRaw($"col = {token}", provider, Ref);
        Assert.Contains(expected, got);
    }

    // === 非 token ===
    [Theory]
    [InlineData("Plain text")]
    [InlineData("2026-04-26")]
    [InlineData("123")]
    [InlineData("$YESTERDAY")]
    [InlineData("${UNKNOWN}")]
    public void Non_token_returns_null(string raw)
    {
        Assert.Null(DateTokenResolver.TryResolve(raw, Ref));
    }

    // === Raw SQL substitution ===
    [Fact]
    public void Substitute_raw_oracle_uses_to_timestamp()
    {
        var sql = "CreatedAt >= ${YESTERDAY} AND CreatedAt <= ${YESTERDAY_END}";
        var got = DateTokenResolver.SubstituteRaw(sql, DbProviderType.Oracle, Ref);
        Assert.Contains("TO_TIMESTAMP('2026-04-25 00:00:00.000'", got);
        Assert.Contains("TO_TIMESTAMP('2026-04-25 23:59:59.999'", got);
        Assert.DoesNotContain("${", got);
    }

    [Fact]
    public void Substitute_raw_sqlserver_uses_string_literal()
    {
        var sql = "CreatedAt BETWEEN ${LAST_MONTH_START} AND ${LAST_MONTH_END}";
        var got = DateTokenResolver.SubstituteRaw(sql, DbProviderType.SqlServer, Ref);
        Assert.Contains("'2026-03-01 00:00:00.000'", got);
        Assert.Contains("'2026-03-31 23:59:59.999'", got);
    }

    [Fact]
    public void Substitute_with_format_yields_quoted_string()
    {
        var sql = "DateKey = ${TODAY:yyyyMMdd}";
        var got = DateTokenResolver.SubstituteRaw(sql, DbProviderType.SqlServer, Ref);
        Assert.Contains("'20260426'", got);
    }

    [Fact]
    public void Substitute_unknown_token_left_intact()
    {
        var sql = "X = ${UNKNOWN_TOKEN}";
        var got = DateTokenResolver.SubstituteRaw(sql, DbProviderType.Oracle, Ref);
        Assert.Contains("${UNKNOWN_TOKEN}", got);
    }
}
