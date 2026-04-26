namespace EtlTool.Core.Engine;

public enum RowCountAssertionPolicy
{
    Ignore = 0,
    Warn = 1,
    Fail = 2,
}

public sealed record RowCountAssertionResult(
    bool Passed,
    string? Violation);

public static class RowCountAssertion
{
    /// <summary>
    /// 判斷 actualRowsRead 是否符合 [min, max]。
    /// - policy=Ignore → 永遠 Passed=true, Violation=null
    /// - min/max 為 null 表示該方向不限
    /// - 違反時 Violation 描述（例：「實際讀取 0 筆，少於最小 100 筆」）
    /// 驗證：min &lt; 0 或 max &lt; 0 → ArgumentException
    /// 驗證：min > max → ArgumentException
    /// </summary>
    public static RowCountAssertionResult Check(
        long actualRowsRead,
        long? min,
        long? max,
        RowCountAssertionPolicy policy)
    {
        if (min.HasValue && min.Value < 0)
            throw new ArgumentException("min must be >= 0.", nameof(min));

        if (max.HasValue && max.Value < 0)
            throw new ArgumentException("max must be >= 0.", nameof(max));

        if (min.HasValue && max.HasValue && min.Value > max.Value)
            throw new ArgumentException("min must be <= max.", nameof(min));

        if (policy == RowCountAssertionPolicy.Ignore)
            return new RowCountAssertionResult(true, null);

        if (min.HasValue && actualRowsRead < min.Value)
            return new RowCountAssertionResult(
                false,
                $"實際讀取 {actualRowsRead} 筆，少於最小 {min.Value} 筆");

        if (max.HasValue && actualRowsRead > max.Value)
            return new RowCountAssertionResult(
                false,
                $"實際讀取 {actualRowsRead} 筆，超過最大 {max.Value} 筆");

        return new RowCountAssertionResult(true, null);
    }
}
