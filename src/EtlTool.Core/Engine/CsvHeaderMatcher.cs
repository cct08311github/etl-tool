namespace EtlTool.Core.Engine;

/// <summary>
/// 把 task.Mappings.SourceColumn（使用者期望的欄位名）對到實際檔案 header 欄位。
/// 依序試以下策略，先命中誰就回誰：
///
///   1. ExactMatch          — 一字不差（最強信心）
///   2. CaseInsensitive     — 忽略大小寫（"id" ↔ "ID"）
///   3. NormalizedCanonical — 移除 _ / - / 空白後忽略大小寫
///                          （"first_name" ↔ "FirstName" ↔ "first-name" ↔ "First Name"）
///   4. Levenshtein ≤ 2     — 拼字錯一兩個字（"customer" ↔ "custmer"）
///
/// 不命中 → null。呼叫端可顯示「找不到對應」讓使用者手動處理。
///
/// 為什麼分這麼多層？
///   - 銀行的舊系統 / 第三方檔常因匯出工具不同造成 case 與 separator 微差
///   - 一律提供置信度（Strategy）讓 UI 用顏色標示「強建議 / 弱建議」
///
/// 不做 substring / contains — 那容易誤殺（"id" 會比對到 "user_id"、"order_id" 全部）。
/// </summary>
public static class CsvHeaderMatcher
{
    public enum MatchStrategy
    {
        None = 0,
        ExactMatch = 1,
        CaseInsensitive = 2,
        NormalizedCanonical = 3,
        FuzzyLevenshtein = 4,
    }

    public sealed record Suggestion(string Original, string? Suggested, MatchStrategy Strategy, int? Distance);

    /// <summary>
    /// 對單一期望欄位找最佳匹配。actualHeaders 是實際檔案的 header（保留原大小寫）。
    /// </summary>
    public static Suggestion FindMatch(string expected, IReadOnlyList<string> actualHeaders)
    {
        if (string.IsNullOrEmpty(expected) || actualHeaders.Count == 0)
            return new Suggestion(expected, null, MatchStrategy.None, null);

        // 1. Exact
        foreach (var h in actualHeaders)
        {
            if (string.Equals(h, expected, StringComparison.Ordinal))
                return new Suggestion(expected, h, MatchStrategy.ExactMatch, 0);
        }

        // 2. Case-insensitive
        foreach (var h in actualHeaders)
        {
            if (string.Equals(h, expected, StringComparison.OrdinalIgnoreCase))
                return new Suggestion(expected, h, MatchStrategy.CaseInsensitive, 0);
        }

        // 3. Normalized canonical（去掉 _ / - / 空白）
        var canonExpected = Canonical(expected);
        foreach (var h in actualHeaders)
        {
            if (string.Equals(Canonical(h), canonExpected, StringComparison.OrdinalIgnoreCase))
                return new Suggestion(expected, h, MatchStrategy.NormalizedCanonical, 0);
        }

        // 4. Levenshtein 距離 ≤ 2（防止「Customer」誤建議成「Vendor」這種完全不同的）
        string? bestHeader = null;
        int bestDist = int.MaxValue;
        foreach (var h in actualHeaders)
        {
            var d = Levenshtein(expected.ToLowerInvariant(), h.ToLowerInvariant());
            if (d < bestDist) { bestDist = d; bestHeader = h; }
        }
        if (bestHeader is not null && bestDist <= 2 && bestDist < expected.Length)
        {
            return new Suggestion(expected, bestHeader, MatchStrategy.FuzzyLevenshtein, bestDist);
        }

        return new Suggestion(expected, null, MatchStrategy.None, null);
    }

    /// <summary>批次：對一組期望欄位都找匹配。</summary>
    public static List<Suggestion> FindMatches(IEnumerable<string> expected, IReadOnlyList<string> actualHeaders)
        => expected.Select(e => FindMatch(e, actualHeaders)).ToList();

    private static string Canonical(string s)
    {
        var chars = s.Where(c => c != '_' && c != '-' && !char.IsWhiteSpace(c));
        return new string(chars.ToArray());
    }

    /// <summary>
    /// 標準 Levenshtein O(n*m) 實作。對銀行欄位名（多半 ≤ 30 字元）速度足夠。
    /// </summary>
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }

    public static string StrategyLabel(MatchStrategy s) => s switch
    {
        MatchStrategy.ExactMatch => "✓ 完全相符",
        MatchStrategy.CaseInsensitive => "≈ 大小寫差異",
        MatchStrategy.NormalizedCanonical => "≈ 命名風格差異 (_/- / camelCase)",
        MatchStrategy.FuzzyLevenshtein => "? 拼字相近（建議檢查）",
        MatchStrategy.None => "✗ 找不到對應",
        _ => s.ToString(),
    };
}
