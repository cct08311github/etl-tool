using EtlTool.Core.Engine;

namespace EtlTool.Tests;

/// <summary>
/// 用固定 reference time 對全部 token 取值並用 explicit assert 比對「人工驗算」結果。
/// 任何一條 token 的語意對不上預期就會在這裡被卡住。
///
/// 參考時間：2026-04-26 14:35:07.890 (Sunday，Q2 中段)
/// </summary>
public class AllTokensSnapshotTest
{
    private static readonly DateTime Ref = new(2026, 4, 26, 14, 35, 7, 890);

    [Theory]
    // 名稱類：今 / 昨 / 明
    [InlineData("${TODAY}",          "2026-04-26 00:00:00.000")]
    [InlineData("${TODAY_END}",      "2026-04-26 23:59:59.999")]
    [InlineData("${YESTERDAY}",      "2026-04-25 00:00:00.000")]
    [InlineData("${YESTERDAY_END}",  "2026-04-25 23:59:59.999")]
    [InlineData("${TOMORROW}",       "2026-04-27 00:00:00.000")]
    [InlineData("${TOMORROW_END}",   "2026-04-27 23:59:59.999")]
    [InlineData("${NOW}",            "2026-04-26 14:35:07.890")]

    // 小時
    [InlineData("${HOUR_START}",     "2026-04-26 14:00:00.000")]
    [InlineData("${HOUR_END}",       "2026-04-26 14:59:59.999")]

    // 週 (週一為起；2026-04-26 = Sunday → 屬於 4/20 ~ 4/26 那週)
    [InlineData("${WEEK_START}",     "2026-04-20 00:00:00.000")]
    [InlineData("${WEEK_END}",       "2026-04-26 23:59:59.999")]
    [InlineData("${LAST_WEEK_START}","2026-04-13 00:00:00.000")]
    [InlineData("${LAST_WEEK_END}",  "2026-04-19 23:59:59.999")]

    // 月
    [InlineData("${MONTH_START}",      "2026-04-01 00:00:00.000")]
    [InlineData("${MONTH_END}",        "2026-04-30 23:59:59.999")]
    [InlineData("${LAST_MONTH_START}", "2026-03-01 00:00:00.000")]
    [InlineData("${LAST_MONTH_END}",   "2026-03-31 23:59:59.999")]

    // 季 (Q2 = Apr~Jun)
    [InlineData("${QUARTER_START}",      "2026-04-01 00:00:00.000")]
    [InlineData("${QUARTER_END}",        "2026-06-30 23:59:59.999")]
    [InlineData("${LAST_QUARTER_START}", "2026-01-01 00:00:00.000")]
    [InlineData("${LAST_QUARTER_END}",   "2026-03-31 23:59:59.999")]

    // 年
    [InlineData("${YEAR_START}",      "2026-01-01 00:00:00.000")]
    [InlineData("${YEAR_END}",        "2026-12-31 23:59:59.999")]
    [InlineData("${LAST_YEAR_START}", "2025-01-01 00:00:00.000")]
    [InlineData("${LAST_YEAR_END}",   "2025-12-31 23:59:59.999")]

    // 同期比較 (point-in-time, 保留時分秒) — 與 X_AGO_1 不同（後者是 date-only）
    [InlineData("${YOY}", "2025-04-26 14:35:07.890")]
    [InlineData("${MOM}", "2026-03-26 14:35:07.890")]
    [InlineData("${WOW}", "2026-04-19 14:35:07.890")]
    [InlineData("${DOD}", "2026-04-25 14:35:07.890")]

    // Period-to-date 別名（= 對應 START）
    [InlineData("${YTD}", "2026-01-01 00:00:00.000")]
    [InlineData("${MTD}", "2026-04-01 00:00:00.000")]
    [InlineData("${WTD}", "2026-04-20 00:00:00.000")]
    [InlineData("${DTD}", "2026-04-26 00:00:00.000")]

    // 相對偏移：分/時保留時分秒；天/週/月/年 = date only (00:00)
    [InlineData("${MINUTES_AGO_15}",    "2026-04-26 14:20:07.890")]
    [InlineData("${MINUTES_AGO_60}",    "2026-04-26 13:35:07.890")]
    [InlineData("${HOURS_AGO_3}",       "2026-04-26 11:35:07.890")]
    [InlineData("${HOURS_AGO_24}",      "2026-04-25 14:35:07.890")]
    [InlineData("${DAYS_AGO_1}",        "2026-04-25 00:00:00.000")]
    [InlineData("${DAYS_AGO_7}",        "2026-04-19 00:00:00.000")]
    [InlineData("${WEEKS_AGO_1}",       "2026-04-19 00:00:00.000")]
    [InlineData("${WEEKS_AGO_4}",       "2026-03-29 00:00:00.000")]
    [InlineData("${MONTHS_AGO_1}",      "2026-03-26 00:00:00.000")]
    [InlineData("${MONTHS_AGO_14}",     "2025-02-26 00:00:00.000")]
    [InlineData("${YEARS_AGO_1}",       "2025-04-26 00:00:00.000")]
    [InlineData("${YEARS_AGO_5}",       "2021-04-26 00:00:00.000")]
    public void Token_resolves_to_exact_value(string token, string expected)
    {
        var got = DateTokenResolver.TryResolve(token, Ref);
        var dt = Assert.IsType<DateTime>(got);
        Assert.Equal(
            expected,
            dt.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 邊界一致性：所有 *_END token 必須恰好等於「下個期間第一刻 - 1ms」。
    /// 顯式列出每對 (end, next-start) 配對，避免推理歧義。
    /// </summary>
    [Fact]
    public void End_tokens_are_one_ms_before_next_period_start()
    {
        DateTime R(string t) => (DateTime)DateTokenResolver.TryResolve(t, Ref)!;

        Assert.Equal(R("${TOMORROW}").AddMilliseconds(-1),                   R("${TODAY_END}"));
        Assert.Equal(R("${TODAY}").AddMilliseconds(-1),                      R("${YESTERDAY_END}"));
        Assert.Equal(R("${HOUR_START}").AddHours(1).AddMilliseconds(-1),     R("${HOUR_END}"));

        // 本週 = 週一到週日；WEEK_END = 下週一 - 1ms
        Assert.Equal(R("${WEEK_START}").AddDays(7).AddMilliseconds(-1),      R("${WEEK_END}"));
        Assert.Equal(R("${WEEK_START}").AddMilliseconds(-1),                 R("${LAST_WEEK_END}"));

        Assert.Equal(R("${MONTH_START}").AddMonths(1).AddMilliseconds(-1),   R("${MONTH_END}"));
        Assert.Equal(R("${MONTH_START}").AddMilliseconds(-1),                R("${LAST_MONTH_END}"));

        Assert.Equal(R("${QUARTER_START}").AddMonths(3).AddMilliseconds(-1), R("${QUARTER_END}"));
        Assert.Equal(R("${QUARTER_START}").AddMilliseconds(-1),              R("${LAST_QUARTER_END}"));

        Assert.Equal(R("${YEAR_START}").AddYears(1).AddMilliseconds(-1),     R("${YEAR_END}"));
        Assert.Equal(R("${YEAR_START}").AddMilliseconds(-1),                 R("${LAST_YEAR_END}"));
    }

    /// <summary>
    /// 同期比較 vs *_AGO_N：前者保留 time-of-day、後者 date-only。
    /// 這是刻意設計，但也是唯一容易讓使用者混淆的點。
    /// </summary>
    [Fact]
    public void YOY_preserves_time_but_YEARS_AGO_1_strips_to_midnight()
    {
        var yoy = (DateTime)DateTokenResolver.TryResolve("${YOY}", Ref)!;
        var yearsAgo1 = (DateTime)DateTokenResolver.TryResolve("${YEARS_AGO_1}", Ref)!;
        Assert.NotEqual(yoy, yearsAgo1);
        Assert.Equal(yoy.Date, yearsAgo1);             // YEARS_AGO_1 = YOY.Date
        Assert.Equal(Ref.TimeOfDay, yoy.TimeOfDay);    // YOY 保留時分秒
        Assert.Equal(TimeSpan.Zero, yearsAgo1.TimeOfDay);
    }

    /// <summary>
    /// 別名一致性：YTD/MTD/WTD/DTD 必須等於對應的 *_START / TODAY
    /// </summary>
    [Theory]
    [InlineData("${YTD}", "${YEAR_START}")]
    [InlineData("${MTD}", "${MONTH_START}")]
    [InlineData("${WTD}", "${WEEK_START}")]
    [InlineData("${DTD}", "${TODAY}")]
    public void Period_to_date_aliases_match_period_starts(string alias, string canonical)
    {
        var a = DateTokenResolver.TryResolve(alias, Ref);
        var c = DateTokenResolver.TryResolve(canonical, Ref);
        Assert.Equal(c, a);
    }
}
