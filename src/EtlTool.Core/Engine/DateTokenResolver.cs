using System.Globalization;
using System.Text.RegularExpressions;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把使用者輸入的「日期變數」token 解析成 DateTime（無格式）或 字串（帶 :format 後綴）。
///
/// 形式：
///   ${TOKEN}              → DateTime
///   ${TOKEN:yyyyMMdd}     → 格式化字串（給 VARCHAR 日期欄位用）
///
/// Token 一覽：
///   今 / 昨 / 明：           TODAY, TODAY_END, YESTERDAY, YESTERDAY_END, TOMORROW
///   當下：                   NOW
///   小時：                   HOUR_START, HOUR_END
///   週：                     WEEK_START (週一), WEEK_END (下週一-1ms),
///                            LAST_WEEK_START, LAST_WEEK_END
///   月：                     MONTH_START, MONTH_END,
///                            LAST_MONTH_START, LAST_MONTH_END
///   季：                     QUARTER_START, QUARTER_END,
///                            LAST_QUARTER_START, LAST_QUARTER_END
///   年：                     YEAR_START, YEAR_END,
///                            LAST_YEAR_START, LAST_YEAR_END
///   相對偏移（N 任填整數）： MINUTES_AGO_N, HOURS_AGO_N, DAYS_AGO_N,
///                            WEEKS_AGO_N, MONTHS_AGO_N, YEARS_AGO_N
///
/// 設計：
///   - 「期間結尾」一律是「下個期間第一刻 - 1 毫秒」(精度 .999) 以利 BETWEEN
///   - 用 DateTime.Now (local time) 為基準
///   - 排程觸發時刻才是基準，不是任務建立時
/// </summary>
public static class DateTokenResolver
{
    private static readonly Regex TokenRegex = new(
        @"^\$\{(?<token>[A-Z_0-9]+)(?::(?<fmt>[^}]+))?\}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubstituteRegex = new(
        @"\$\{(?<token>[A-Z_0-9]+)(?::(?<fmt>[^}]+))?\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RelRegex = new(
        @"^(?<unit>MINUTES|HOURS|DAYS|WEEKS|MONTHS|YEARS)_AGO_(?<n>\d+)$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 解析整個 token 字串。
    /// - 帶 :format → 回傳 string (formatted)
    /// - 不帶 → 回傳 DateTime
    /// - 不是 token → 回傳 null
    /// </summary>
    public static object? TryResolve(string? raw, DateTime? referenceNow = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = TokenRegex.Match(raw.Trim());
        if (!m.Success) return null;

        var dt = ResolveByName(m.Groups["token"].Value.ToUpperInvariant(), referenceNow ?? DateTime.Now);
        if (dt is null) return null;

        var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
        return string.IsNullOrEmpty(fmt) ? (object)dt.Value : dt.Value.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把任意純文字（檔案路徑 / 檔名 glob / 不需要 SQL quoting 的字串）裡的
    /// ${TOKEN} 替換成「未加引號」的字面值。
    ///
    /// 用法：
    ///   /data/inbox/${YYYY:yyyy-MM-dd}/orders.csv  →  /data/inbox/2026-04-27/orders.csv
    ///   orders_${TODAY:yyyyMMdd}.csv                →  orders_20260427.csv
    ///   journal_${YESTERDAY:yyyy-MM-dd}_*.txt       →  journal_2026-04-26_*.txt
    ///
    /// 不帶 :format 的 token 會被替換成 yyyy-MM-dd 預設格式（檔名通常不需要時間部分）。
    /// 不認得的 token 不替換（保留原樣 ${...}），方便排錯。
    /// </summary>
    public static string SubstituteFilePath(string path, DateTime? referenceNow = null)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var now = referenceNow ?? DateTime.Now;
        return SubstituteRegex.Replace(path, m =>
        {
            var dt = ResolveByName(m.Groups["token"].Value.ToUpperInvariant(), now);
            if (dt is null) return m.Value;  // 不認得 → 原樣保留
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : "yyyy-MM-dd";
            return dt.Value.ToString(fmt, CultureInfo.InvariantCulture);
        });
    }

    /// <summary>
    /// 把 SQL 字串中的 ${TOKEN} 全部替換成 provider-specific 字面值。
    /// - 帶 :format → 替換成單引號字串（無 escape；日期格式不會含單引號所以安全）
    /// - 不帶 → 替換成 DATE/TIMESTAMP 字面值
    /// </summary>
    public static string SubstituteRaw(string sql, DbProviderType provider, DateTime? referenceNow = null)
    {
        if (string.IsNullOrEmpty(sql)) return sql;
        var now = referenceNow ?? DateTime.Now;

        return SubstituteRegex.Replace(sql, m =>
        {
            var dt = ResolveByName(m.Groups["token"].Value.ToUpperInvariant(), now);
            if (dt is null) return m.Value;

            if (m.Groups["fmt"].Success)
            {
                var formatted = dt.Value.ToString(m.Groups["fmt"].Value, CultureInfo.InvariantCulture);
                return $"'{formatted}'";
            }
            return FormatLiteral(dt.Value, provider);
        });
    }

    public static string FormatLiteral(DateTime dt, DbProviderType provider) => provider switch
    {
        DbProviderType.Oracle =>
            $"TO_TIMESTAMP('{dt:yyyy-MM-dd HH:mm:ss.fff}', 'YYYY-MM-DD HH24:MI:SS.FF3')",
        DbProviderType.SqlServer =>
            $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        _ => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
    };

    private static DateTime? ResolveByName(string token, DateTime now)
    {
        var today = now.Date;

        // 一些重複用的計算
        DateTime weekStart(DateTime d) =>
            d.AddDays(-((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7);

        DateTime quarterStart(DateTime d) =>
            new(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1);

        // 期間結尾 = 下個期間 first instant - 1ms
        DateTime endOf(DateTime startOfNextPeriod) =>
            startOfNextPeriod.AddMilliseconds(-1);

        return token switch
        {
            // 今 / 昨 / 明
            "TODAY"          => today,
            "TODAY_END"      => (DateTime?)endOf(today.AddDays(1)),
            "YESTERDAY"      => today.AddDays(-1),
            "YESTERDAY_END"  => (DateTime?)endOf(today),
            "TOMORROW"       => today.AddDays(1),
            "TOMORROW_END"   => (DateTime?)endOf(today.AddDays(2)),

            "NOW"            => now,

            // 小時
            "HOUR_START"     => new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind),
            "HOUR_END"       => endOf(new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind).AddHours(1)),

            // 週
            "WEEK_START"     => weekStart(today),
            "WEEK_END"       => endOf(weekStart(today).AddDays(7)),
            "LAST_WEEK_START"=> weekStart(today).AddDays(-7),
            "LAST_WEEK_END"  => endOf(weekStart(today)),

            // 月
            "MONTH_START"    => new(now.Year, now.Month, 1),
            "MONTH_END"      => endOf(new DateTime(now.Year, now.Month, 1).AddMonths(1)),
            "LAST_MONTH_START" => new DateTime(now.Year, now.Month, 1).AddMonths(-1),
            "LAST_MONTH_END"   => endOf(new DateTime(now.Year, now.Month, 1)),

            // 季
            "QUARTER_START"     => quarterStart(today),
            "QUARTER_END"       => endOf(quarterStart(today).AddMonths(3)),
            "LAST_QUARTER_START"=> quarterStart(today).AddMonths(-3),
            "LAST_QUARTER_END"  => endOf(quarterStart(today)),

            // 年
            "YEAR_START"        => new(now.Year, 1, 1),
            "YEAR_END"          => endOf(new DateTime(now.Year + 1, 1, 1)),
            "LAST_YEAR_START"   => new(now.Year - 1, 1, 1),
            "LAST_YEAR_END"     => endOf(new DateTime(now.Year, 1, 1)),

            // 同期比較：「同樣時刻 N 個週期前」(point-in-time)，用於 YoY / MoM 比對基準
            "YOY"   => now.AddYears(-1),
            "MOM"   => now.AddMonths(-1),
            "WOW"   => now.AddDays(-7),
            "DOD"   => now.AddDays(-1),

            // Period-to-date：起始點別名（範圍寫法：[YTD, NOW] / [MTD, NOW] 等）
            "YTD"   => new(now.Year, 1, 1),                   // = YEAR_START
            "MTD"   => new(now.Year, now.Month, 1),           // = MONTH_START
            "WTD"   => weekStart(today),                      // = WEEK_START
            "DTD"   => today,                                 // = TODAY

            _ => TryParseRelative(token, now),
        };
    }

    private static DateTime? TryParseRelative(string token, DateTime now)
    {
        var m = RelRegex.Match(token);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
        return m.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "MINUTES" => now.AddMinutes(-n),
            "HOURS"   => now.AddHours(-n),
            "DAYS"    => now.Date.AddDays(-n),
            "WEEKS"   => now.Date.AddDays(-n * 7),
            "MONTHS"  => now.Date.AddMonths(-n),
            "YEARS"   => now.Date.AddYears(-n),
            _ => null,
        };
    }
}
