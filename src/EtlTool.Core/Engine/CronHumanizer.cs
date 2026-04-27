namespace EtlTool.Core.Engine;

/// <summary>
/// 把 Quartz cron 表達式（6/7 欄位「秒 分 時 日 月 週 [年]」）轉成中文人類可讀說明。
/// 不是完整的 cron parser — 只認得幾種常見模式（CronEditor 預設值 + 簡單變體）。
/// 不認得的就回 null，讓 UI 退化顯示原始 cron。
///
/// 設計理由：完整 cron 描述需要 ~500 行程式碼且不少邊界 case；
/// 這裡只解 admin 透過 CronEditor 預設常按出來的幾個 pattern + 一些常見手寫。
/// 其他複雜表達式（多時間點、L、W、#）admin 自己看 cron 字串即可。
/// </summary>
public static class CronHumanizer
{
    /// <summary>
    /// 嘗試把 cron 翻成中文。Quartz 格式：sec min hour day month dow [year]。
    /// 回傳：成功 → 中文描述；不認得 → null。
    /// </summary>
    public static string? Humanize(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return null;
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return null;  // Quartz 至少要 6 欄

        var sec = parts[0];
        var min = parts[1];
        var hour = parts[2];
        var dom = parts[3];     // day-of-month
        var mon = parts[4];
        var dow = parts[5];     // day-of-week

        // 模式 1：每 N 分鐘    → "0 0/N * * * ?" 或 "0 */N * * * ?"
        if (sec == "0" && hour == "*" && dom == "*" && mon == "*" && (dow == "?" || dow == "*"))
        {
            var n = ParseStepInterval(min);
            if (n is { } step) return $"每 {step} 分鐘";
            if (min == "*") return "每分鐘";
        }

        // 模式 2：每小時整點   → "0 0 * * * ?"
        if (sec == "0" && min == "0" && hour == "*" && dom == "*" && mon == "*" && (dow == "?" || dow == "*"))
            return "每小時整點";

        // 模式 3：每 N 小時整點 → "0 0 0/N * * ?"
        if (sec == "0" && min == "0" && dom == "*" && mon == "*" && (dow == "?" || dow == "*"))
        {
            var n = ParseStepInterval(hour);
            if (n is { } step) return $"每 {step} 小時整點";
        }

        // 模式 4：每天固定時刻  → "0 M H * * ?" (H, M 都是固定值)
        if (sec == "0" && IsFixed(min) && IsFixed(hour) && dom == "*" && mon == "*" && (dow == "?" || dow == "*"))
        {
            return $"每天 {Pad(hour)}:{Pad(min)}";
        }

        // 模式 5：每週固定一天 + 時刻 → "0 M H ? * MON" 等
        if (sec == "0" && IsFixed(min) && IsFixed(hour) && dom == "?" && mon == "*" && IsKnownDow(dow))
        {
            return $"每週{ChineseDow(dow)} {Pad(hour)}:{Pad(min)}";
        }

        // 模式 6：每月某日固定時刻 → "0 M H D * ?"  (D 是 1-31)
        if (sec == "0" && IsFixed(min) && IsFixed(hour) && IsFixed(dom) && mon == "*" && (dow == "?" || dow == "*"))
        {
            return $"每月 {dom} 號 {Pad(hour)}:{Pad(min)}";
        }

        return null;
    }

    private static bool IsFixed(string field)
        => int.TryParse(field, out _);

    private static string Pad(string field)
        => int.TryParse(field, out var n) ? n.ToString("D2") : field;

    /// <summary>"0/N" 或 "*/N" → N；否則 null。</summary>
    private static int? ParseStepInterval(string field)
    {
        // 0/5 形式
        if (field.StartsWith("0/", StringComparison.Ordinal))
        {
            if (int.TryParse(field[2..], out var n) && n > 0) return n;
        }
        // */5 形式
        if (field.StartsWith("*/", StringComparison.Ordinal))
        {
            if (int.TryParse(field[2..], out var n) && n > 0) return n;
        }
        return null;
    }

    private static bool IsKnownDow(string dow) => ChineseDow(dow) is not null;

    /// <summary>"MON" / "TUE" ... → "一" / "二" 等；不認得 → null。</summary>
    private static string? ChineseDow(string dow)
    {
        return dow.Trim().ToUpperInvariant() switch
        {
            "MON" or "1" => "一",
            "TUE" or "2" => "二",
            "WED" or "3" => "三",
            "THU" or "4" => "四",
            "FRI" or "5" => "五",
            "SAT" or "6" => "六",
            "SUN" or "0" or "7" => "日",
            _ => null,
        };
    }
}
