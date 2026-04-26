using System.Globalization;

namespace EtlTool.Core.Scheduling;

/// <summary>
/// 一個維護時段：在這段時間排程觸發的 ETL 會被 skip（手動觸發仍允許）。
/// 從 appsettings 讀，不存 DB 避免高頻變動。
///
/// 例：每天備份視窗 02:00-04:00
///   { "Days": ["All"], "From": "02:00", "To": "04:00", "Reason": "夜間備份" }
///
/// 例：週末整天
///   { "Days": ["Sat", "Sun"], "From": "00:00", "To": "23:59", "Reason": "週末停機" }
///
/// 跨午夜（如 22:00-02:00）也支援：From > To 時自動處理。
/// </summary>
public sealed class MaintenanceWindow
{
    /// <summary>
    /// "All" = 每天；或星期縮寫 ["Mon","Tue",...,"Sun"]。
    /// 大小寫不敏感。空陣列 = 不啟用此 window。
    /// </summary>
    public string[] Days { get; set; } = Array.Empty<string>();

    /// <summary>HH:mm 格式 (24h)。</summary>
    public string From { get; set; } = "00:00";

    /// <summary>HH:mm 格式 (24h)。From > To 表示跨午夜。</summary>
    public string To { get; set; } = "00:00";

    /// <summary>給 audit 訊息用，可空。</summary>
    public string? Reason { get; set; }

    public bool IsActive(DateTime localNow)
    {
        if (Days.Length == 0) return false;

        bool dayMatches = Days.Any(d =>
            d.Equals("All", StringComparison.OrdinalIgnoreCase)
            || ParseDay(d) == localNow.DayOfWeek);
        if (!dayMatches) return false;

        if (!TryParseTime(From, out var from)) return false;
        if (!TryParseTime(To, out var to)) return false;

        var t = localNow.TimeOfDay;
        // 跨午夜：from=22:00 to=02:00 → 22:00 之後 OR 02:00 之前
        if (from > to)
            return t >= from || t < to;
        return t >= from && t < to;
    }

    private static DayOfWeek? ParseDay(string s) => s.Trim().ToLowerInvariant() switch
    {
        "mon" or "monday"    => DayOfWeek.Monday,
        "tue" or "tuesday"   => DayOfWeek.Tuesday,
        "wed" or "wednesday" => DayOfWeek.Wednesday,
        "thu" or "thursday"  => DayOfWeek.Thursday,
        "fri" or "friday"    => DayOfWeek.Friday,
        "sat" or "saturday"  => DayOfWeek.Saturday,
        "sun" or "sunday"    => DayOfWeek.Sunday,
        _ => null,
    };

    private static bool TryParseTime(string s, out TimeSpan t)
        => TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out t);
}

public sealed class MaintenanceWindowsOptions
{
    public List<MaintenanceWindow> Windows { get; set; } = new();

    /// <summary>傳回第一個符合的 window；都不符合則 null。</summary>
    public MaintenanceWindow? CurrentlyActive(DateTime localNow)
        => Windows.FirstOrDefault(w => w.IsActive(localNow));
}
