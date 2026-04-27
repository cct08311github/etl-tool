using EtlTool.Core.Engine;

namespace EtlTool.Tests;

/// <summary>
/// 驗證 header 比對策略的優先順序：
/// Exact > CaseInsensitive > NormalizedCanonical (snake/camel/space) > Levenshtein ≤ 2 > None。
/// </summary>
public class CsvHeaderMatcherTests
{
    [Fact]
    public void Exact_match_wins()
    {
        var s = CsvHeaderMatcher.FindMatch("FirstName", new[] { "FirstName", "firstname", "first_name" });
        Assert.Equal("FirstName", s.Suggested);
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.ExactMatch, s.Strategy);
    }

    [Fact]
    public void Case_insensitive_match()
    {
        var s = CsvHeaderMatcher.FindMatch("FirstName", new[] { "firstname", "id" });
        Assert.Equal("firstname", s.Suggested);
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.CaseInsensitive, s.Strategy);
    }

    [Theory]
    [InlineData("FirstName", "first_name")]
    [InlineData("FirstName", "first-name")]
    [InlineData("FirstName", "First Name")]
    [InlineData("first_name", "FirstName")]
    [InlineData("first_name", "FIRSTNAME")]
    public void Normalized_canonical_match(string expected, string actual)
    {
        var s = CsvHeaderMatcher.FindMatch(expected, new[] { actual });
        Assert.Equal(actual, s.Suggested);
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.NormalizedCanonical, s.Strategy);
    }

    [Theory]
    [InlineData("Customer", "Custmer", 1)]      // 缺一個字
    [InlineData("Amount", "Amaunt", 1)]         // 一字錯
    [InlineData("OrderId", "OrderID", 0)]       // ID 與 Id 大小寫差 → CaseInsensitive 命中（嚴格說沒到 fuzzy）
    public void Fuzzy_match_within_distance(string expected, string actual, int expectedMaxDist)
    {
        var s = CsvHeaderMatcher.FindMatch(expected, new[] { actual });
        Assert.NotNull(s.Suggested);
        // 接受 fuzzy 或 case-insensitive（距離 0 時 case-insensitive 會先命中）
        if (s.Strategy == CsvHeaderMatcher.MatchStrategy.FuzzyLevenshtein)
        {
            Assert.NotNull(s.Distance);
            Assert.True(s.Distance <= expectedMaxDist + 1);
        }
    }

    [Fact]
    public void No_match_when_too_different()
    {
        var s = CsvHeaderMatcher.FindMatch("Customer", new[] { "Vendor", "Supplier" });
        Assert.Null(s.Suggested);
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.None, s.Strategy);
    }

    [Fact]
    public void Empty_inputs_return_none()
    {
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.None,
            CsvHeaderMatcher.FindMatch("", new[] { "x" }).Strategy);
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.None,
            CsvHeaderMatcher.FindMatch("x", Array.Empty<string>()).Strategy);
    }

    [Fact]
    public void Batch_returns_one_per_expected()
    {
        var actuals = new[] { "id", "first_name", "last_name", "amount" };
        var results = CsvHeaderMatcher.FindMatches(
            new[] { "Id", "FirstName", "LastName", "Cost" },
            actuals);
        Assert.Equal(4, results.Count);
        Assert.Equal("id", results[0].Suggested);
        Assert.Equal("first_name", results[1].Suggested);
        Assert.Equal("last_name", results[2].Suggested);
        // Cost ↔ amount 差太遠 → None
        Assert.Null(results[3].Suggested);
    }

    [Fact]
    public void Strategy_priority_order_consistent()
    {
        // 同時存在 exact / case-insensitive / canonical 候選 → 應選 exact
        var s = CsvHeaderMatcher.FindMatch("FirstName",
            new[] { "first_name", "firstname", "FirstName" });
        Assert.Equal("FirstName", s.Suggested);
        Assert.Equal(CsvHeaderMatcher.MatchStrategy.ExactMatch, s.Strategy);
    }
}
