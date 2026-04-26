using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class SqlRendererTests
{
    [Fact]
    public void Renders_string_with_quote_escape()
    {
        var ps = new (string, object?)[] { ("f0", "O'Brien") };
        var got = SqlRenderer.Render("SELECT * FROM T WHERE name = @f0", ps, DbProviderType.SqlServer, "@");
        Assert.Equal("SELECT * FROM T WHERE name = 'O''Brien'", got);
    }

    [Fact]
    public void Renders_int_inline()
    {
        var ps = new (string, object?)[] { ("f0", 42L) };
        Assert.Equal("WHERE id = 42",
            SqlRenderer.Render("WHERE id = @f0", ps, DbProviderType.SqlServer, "@"));
    }

    [Fact]
    public void Renders_decimal_with_invariant_culture()
    {
        var ps = new (string, object?)[] { ("f0", 1234.56m) };
        Assert.Equal("WHERE price >= 1234.56",
            SqlRenderer.Render("WHERE price >= @f0", ps, DbProviderType.SqlServer, "@"));
    }

    [Fact]
    public void Renders_datetime_oracle_uses_to_timestamp()
    {
        var ps = new (string, object?)[] { ("f0", new DateTime(2026, 4, 26, 10, 30, 15)) };
        var got = SqlRenderer.Render("WHERE t > :f0", ps, DbProviderType.Oracle, ":");
        Assert.Contains("TO_TIMESTAMP('2026-04-26 10:30:15.000'", got);
    }

    [Fact]
    public void Renders_null_as_NULL()
    {
        var ps = new (string, object?)[] { ("f0", null) };
        Assert.Equal("WHERE x IS NULL OR x = NULL",
            SqlRenderer.Render("WHERE x IS NULL OR x = @f0", ps, DbProviderType.SqlServer, "@"));
    }

    [Fact]
    public void Renders_bool_as_one_zero()
    {
        var ps = new (string, object?)[] { ("f0", true), ("f1", false) };
        Assert.Equal("WHERE a = 1 AND b = 0",
            SqlRenderer.Render("WHERE a = @f0 AND b = @f1", ps, DbProviderType.SqlServer, "@"));
    }

    [Fact]
    public void Replaces_longest_first_to_avoid_prefix_conflict()
    {
        // f1 might be substring of f10 — must replace f10 first
        var ps = new (string, object?)[] { ("f1", 1L), ("f10", 99L) };
        var got = SqlRenderer.Render("a = :f1, b = :f10", ps, DbProviderType.Oracle, ":");
        Assert.Equal("a = 1, b = 99", got);
    }

    [Fact]
    public void No_params_returns_sql_unchanged()
    {
        Assert.Equal("SELECT 1", SqlRenderer.Render("SELECT 1",
            Array.Empty<(string, object?)>(), DbProviderType.SqlServer, "@"));
    }
}

public class DateComparisonTokenTests
{
    private static readonly DateTime Ref = new(2026, 4, 26, 14, 35, 7);

    [Fact] public void YOY_one_year_ago_same_time()
        => Assert.Equal(new DateTime(2025, 4, 26, 14, 35, 7), DateTokenResolver.TryResolve("${YOY}", Ref));

    [Fact] public void MOM_one_month_ago_same_time()
        => Assert.Equal(new DateTime(2026, 3, 26, 14, 35, 7), DateTokenResolver.TryResolve("${MOM}", Ref));

    [Fact] public void WOW_one_week_ago_same_time()
        => Assert.Equal(new DateTime(2026, 4, 19, 14, 35, 7), DateTokenResolver.TryResolve("${WOW}", Ref));

    [Fact] public void DOD_one_day_ago_same_time()
        => Assert.Equal(new DateTime(2026, 4, 25, 14, 35, 7), DateTokenResolver.TryResolve("${DOD}", Ref));

    [Fact] public void YTD_alias_for_year_start()
        => Assert.Equal(DateTokenResolver.TryResolve("${YEAR_START}", Ref), DateTokenResolver.TryResolve("${YTD}", Ref));

    [Fact] public void MTD_alias_for_month_start()
        => Assert.Equal(DateTokenResolver.TryResolve("${MONTH_START}", Ref), DateTokenResolver.TryResolve("${MTD}", Ref));

    [Fact] public void WTD_alias_for_week_start()
        => Assert.Equal(DateTokenResolver.TryResolve("${WEEK_START}", Ref), DateTokenResolver.TryResolve("${WTD}", Ref));

    [Fact] public void DTD_alias_for_today()
        => Assert.Equal(DateTokenResolver.TryResolve("${TODAY}", Ref), DateTokenResolver.TryResolve("${DTD}", Ref));
}
