using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class SqlRendererTests
{
    // ── string: both DB types use single-quote style with escape ──────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "SELECT * FROM T WHERE name = @f0",
                "SELECT * FROM T WHERE name = 'O''Brien'")]
    [InlineData(DbProviderType.Oracle,    ":", "SELECT * FROM T WHERE name = :f0",
                "SELECT * FROM T WHERE name = 'O''Brien'")]
    public void Renders_string_with_quote_escape(DbProviderType provider, string prefix, string sql, string expected)
    {
        var ps = new (string, object?)[] { ("f0", "O'Brien") };
        Assert.Equal(expected, SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── int (long): provider-independent ─────────────────────────────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "WHERE id = @f0")]
    [InlineData(DbProviderType.Oracle,    ":", "WHERE id = :f0")]
    public void Renders_int_inline(DbProviderType provider, string prefix, string sql)
    {
        var ps = new (string, object?)[] { ("f0", 42L) };
        Assert.Equal("WHERE id = 42", SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── decimal: invariant culture, provider-independent ─────────────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "WHERE price >= @f0")]
    [InlineData(DbProviderType.Oracle,    ":", "WHERE price >= :f0")]
    public void Renders_decimal_with_invariant_culture(DbProviderType provider, string prefix, string sql)
    {
        var ps = new (string, object?)[] { ("f0", 1234.56m) };
        Assert.Equal("WHERE price >= 1234.56", SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── bool: 1/0, provider-independent ──────────────────────────────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "WHERE a = @f0 AND b = @f1")]
    [InlineData(DbProviderType.Oracle,    ":", "WHERE a = :f0 AND b = :f1")]
    public void Renders_bool_as_one_zero(DbProviderType provider, string prefix, string sql)
    {
        var ps = new (string, object?)[] { ("f0", true), ("f1", false) };
        Assert.Equal("WHERE a = 1 AND b = 0", SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── null: both providers render as NULL ───────────────────────────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "WHERE x IS NULL OR x = @f0")]
    [InlineData(DbProviderType.Oracle,    ":", "WHERE x IS NULL OR x = :f0")]
    public void Renders_null_as_NULL(DbProviderType provider, string prefix, string sql)
    {
        var ps = new (string, object?)[] { ("f0", null) };
        Assert.Equal("WHERE x IS NULL OR x = NULL", SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── byte[]: SqlServer → 0xABCD, Oracle → HEXTORAW('ABCD') ─────────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "WHERE hash = @f0", "WHERE hash = 0xABCD")]
    [InlineData(DbProviderType.Oracle,    ":", "WHERE hash = :f0", "WHERE hash = HEXTORAW('ABCD')")]
    public void Renders_bytes_provider_specific(DbProviderType provider, string prefix, string sql, string expected)
    {
        var ps = new (string, object?)[] { ("f0", new byte[] { 0xAB, 0xCD }) };
        Assert.Equal(expected, SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── longest-first to avoid prefix collision ───────────────────────────────
    [Theory]
    [InlineData(DbProviderType.SqlServer, "@", "a = @f1, b = @f10")]
    [InlineData(DbProviderType.Oracle,    ":", "a = :f1, b = :f10")]
    public void Replaces_longest_first_to_avoid_prefix_conflict(DbProviderType provider, string prefix, string sql)
    {
        var ps = new (string, object?)[] { ("f1", 1L), ("f10", 99L) };
        Assert.Equal("a = 1, b = 99", SqlRenderer.Render(sql, ps, provider, prefix));
    }

    // ── datetime: SqlServer uses string literal, Oracle uses TO_TIMESTAMP ─────
    [Fact]
    public void Renders_datetime_oracle_uses_to_timestamp()
    {
        var ps = new (string, object?)[] { ("f0", new DateTime(2026, 4, 26, 10, 30, 15)) };
        var got = SqlRenderer.Render("WHERE t > :f0", ps, DbProviderType.Oracle, ":");
        Assert.Contains("TO_TIMESTAMP('2026-04-26 10:30:15.000'", got);
    }

    [Fact]
    public void Renders_datetime_sqlserver_uses_string_literal()
    {
        var ps = new (string, object?)[] { ("f0", new DateTime(2026, 4, 26, 10, 30, 15)) };
        var got = SqlRenderer.Render("WHERE t > @f0", ps, DbProviderType.SqlServer, "@");
        Assert.Equal("WHERE t > '2026-04-26 10:30:15.000'", got);
    }

    // ── no params: SQL returned unchanged ────────────────────────────────────
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
