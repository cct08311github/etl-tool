using System.Data;
using System.Data.Common;
using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class FilterCompilerTests
{
    private readonly IDbConnector _conn = new StubConnector();

    [Fact]
    public void Empty_filter_yields_empty_where()
    {
        var c = new FilterCompiler(_conn).Compile(null);
        Assert.Equal(string.Empty, c.WhereSql);
        Assert.Empty(c.Parameters);
    }

    [Fact]
    public void Single_eq_condition()
    {
        var node = new FilterCondition { Column = "AGE", Operator = FilterOperator.Eq, Value = "30" };
        var c = new FilterCompiler(_conn).Compile(node);
        Assert.Equal("[AGE] = @f0", c.WhereSql);
        Assert.Single(c.Parameters);
        Assert.Equal(30L, c.Parameters[0].Value);
    }

    // 確認使用者直接在「值」欄位輸入日期字串，會被解析成 DateTime（給 ADO.NET 自動處理型別）
    [Theory]
    [InlineData("2026-04-26",          "ISO dash")]
    [InlineData("2026/04/26",          "slash")]
    [InlineData("2026-04-26 14:30:00", "ISO with time")]
    [InlineData("2026/04/26 14:30",    "slash with time")]
    public void Date_string_parsed_as_DateTime(string input, string _)
    {
        var node = new FilterCondition { Column = "T", Operator = FilterOperator.Gte, Value = input };
        var c = new FilterCompiler(_conn).Compile(node);
        Assert.Single(c.Parameters);
        Assert.IsType<DateTime>(c.Parameters[0].Value);
    }

    [Fact]
    public void Group_with_and_logic()
    {
        var node = new FilterGroup
        {
            Logic = FilterLogic.And,
            Children =
            {
                new FilterCondition { Column = "DEPT_ID", Operator = FilterOperator.In, Values = new() { "10", "20" } },
                new FilterCondition { Column = "STATUS", Operator = FilterOperator.IsNotNull },
            },
        };
        var c = new FilterCompiler(_conn).Compile(node);
        Assert.Equal("([DEPT_ID] IN (@f0,@f1) AND [STATUS] IS NOT NULL)", c.WhereSql);
        Assert.Equal(2, c.Parameters.Count);
    }

    [Fact]
    public void Between_requires_two_values()
    {
        var node = new FilterCondition { Column = "X", Operator = FilterOperator.Between, Values = new() { "1" } };
        Assert.Throws<ArgumentException>(() => new FilterCompiler(_conn).Compile(node));
    }

    [Fact]
    public void Like_uses_string_value()
    {
        var node = new FilterCondition { Column = "NAME", Operator = FilterOperator.Like, Value = "Joe%" };
        var c = new FilterCompiler(_conn).Compile(node);
        Assert.Equal("[NAME] LIKE @f0", c.WhereSql);
        Assert.Equal("Joe%", c.Parameters[0].Value);
    }

    [Fact]
    public void In_with_no_values_throws()
    {
        var node = new FilterCondition { Column = "X", Operator = FilterOperator.In, Values = new() };
        Assert.Throws<ArgumentException>(() => new FilterCompiler(_conn).Compile(node));
    }

    [Fact]
    public void Nested_or_inside_and()
    {
        var node = new FilterGroup
        {
            Logic = FilterLogic.And,
            Children =
            {
                new FilterCondition { Column = "A", Operator = FilterOperator.Eq, Value = "1" },
                new FilterGroup
                {
                    Logic = FilterLogic.Or,
                    Children =
                    {
                        new FilterCondition { Column = "B", Operator = FilterOperator.Eq, Value = "2" },
                        new FilterCondition { Column = "C", Operator = FilterOperator.Eq, Value = "3" },
                    },
                },
            },
        };
        var c = new FilterCompiler(_conn).Compile(node);
        Assert.Equal("([A] = @f0 AND ([B] = @f1 OR [C] = @f2))", c.WhereSql);
        Assert.Equal(3, c.Parameters.Count);
    }

    private sealed class StubConnector : IDbConnector
    {
        public DbProviderType Provider => DbProviderType.SqlServer;
        public string ParameterPrefix => "@";
        public string QuoteIdentifier(string name) => $"[{name}]";
        public string QuoteQualified(string s, string t) => $"[{s}].[{t}]";
        public DbParameter CreateParameter(string name, object? value) => throw new NotImplementedException();
        public Task<DbConnection> OpenAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListTablesAsync(string schema, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string schema, string table, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(string schema, string table, CancellationToken ct) => throw new NotImplementedException();
        public IBulkWriter CreateBulkWriter(DbConnection c, DbTransaction t) => throw new NotImplementedException();
        public IUpsertWriter CreateUpsertWriter(DbConnection c, DbTransaction t) => throw new NotImplementedException();
        public string LimitedSelect(string cols, string from, string? where, int limit) => throw new NotImplementedException();
    }
}

/// <summary>
/// Runs the same FilterCompiler test cases against both SqlServer and Oracle stubs,
/// verifying that quote style and parameter prefix are applied correctly per dialect.
/// </summary>
public class FilterCompilerDialectTests
{
    // ── Stub connectors ───────────────────────────────────────────────────────

    private sealed class SqlServerStub : IDbConnector
    {
        public DbProviderType Provider => DbProviderType.SqlServer;
        public string ParameterPrefix => "@";
        public string QuoteIdentifier(string name) => $"[{name}]";
        public string QuoteQualified(string s, string t) => $"[{s}].[{t}]";
        public DbParameter CreateParameter(string name, object? value) => throw new NotImplementedException();
        public Task<DbConnection> OpenAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListTablesAsync(string schema, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string schema, string table, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(string schema, string table, CancellationToken ct) => throw new NotImplementedException();
        public IBulkWriter CreateBulkWriter(DbConnection c, DbTransaction t) => throw new NotImplementedException();
        public IUpsertWriter CreateUpsertWriter(DbConnection c, DbTransaction t) => throw new NotImplementedException();
        public string LimitedSelect(string cols, string from, string? where, int limit) => throw new NotImplementedException();
    }

    private sealed class OracleStubConnector : IDbConnector
    {
        public DbProviderType Provider => DbProviderType.Oracle;
        public string ParameterPrefix => ":";
        public string QuoteIdentifier(string name) => $"\"{name}\"";
        public string QuoteQualified(string s, string t) => $"\"{s}\".\"{t}\"";
        public DbParameter CreateParameter(string name, object? value) => throw new NotImplementedException();
        public Task<DbConnection> OpenAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListTablesAsync(string schema, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string schema, string table, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(string schema, string table, CancellationToken ct) => throw new NotImplementedException();
        public IBulkWriter CreateBulkWriter(DbConnection c, DbTransaction t) => throw new NotImplementedException();
        public IUpsertWriter CreateUpsertWriter(DbConnection c, DbTransaction t) => throw new NotImplementedException();
        public string LimitedSelect(string cols, string from, string? where, int limit) => throw new NotImplementedException();
    }

    // ── MemberData providers ──────────────────────────────────────────────────

    public static IEnumerable<object[]> BothConnectors()
    {
        yield return new object[] { new SqlServerStub() };
        yield return new object[] { new OracleStubConnector() };
    }

    // ── Dialect-specific expected SQL helpers ─────────────────────────────────

    private static string Q(IDbConnector conn, string col) => conn.QuoteIdentifier(col);
    private static string P(IDbConnector conn, string name) => conn.ParameterPrefix + name;

    // ── Tests ─────────────────────────────────────────────────────────────────

    // Single equality — verifies quote char and param prefix
    [Theory]
    [MemberData(nameof(BothConnectors))]
    public void SingleEq_DialectQuotingAndPrefix(IDbConnector conn)
    {
        var node = new FilterCondition { Column = "AGE", Operator = FilterOperator.Eq, Value = "30" };
        var c = new FilterCompiler(conn).Compile(node);
        Assert.Equal($"{Q(conn, "AGE")} = {P(conn, "f0")}", c.WhereSql);
        Assert.Single(c.Parameters);
        Assert.Equal(30L, c.Parameters[0].Value);
    }

    // IN operator — verifies comma-separated params with correct prefix
    [Theory]
    [MemberData(nameof(BothConnectors))]
    public void InOperator_DialectParams(IDbConnector conn)
    {
        var node = new FilterCondition
        {
            Column = "DEPT_ID",
            Operator = FilterOperator.In,
            Values = new() { "10", "20" },
        };
        var c = new FilterCompiler(conn).Compile(node);
        Assert.Equal(
            $"{Q(conn, "DEPT_ID")} IN ({P(conn, "f0")},{P(conn, "f1")})",
            c.WhereSql);
        Assert.Equal(2, c.Parameters.Count);
    }

    // BETWEEN — two params with correct prefix
    [Theory]
    [MemberData(nameof(BothConnectors))]
    public void Between_DialectParams(IDbConnector conn)
    {
        var node = new FilterCondition
        {
            Column = "SCORE",
            Operator = FilterOperator.Between,
            Values = new() { "50", "100" },
        };
        var c = new FilterCompiler(conn).Compile(node);
        Assert.Equal(
            $"{Q(conn, "SCORE")} BETWEEN {P(conn, "f0")} AND {P(conn, "f1")}",
            c.WhereSql);
        Assert.Equal(2, c.Parameters.Count);
    }

    // Nested AND-OR group — verifies both quoting and prefix throughout the tree
    [Theory]
    [MemberData(nameof(BothConnectors))]
    public void NestedAndOr_DialectQuotingAndPrefix(IDbConnector conn)
    {
        var node = new FilterGroup
        {
            Logic = FilterLogic.And,
            Children =
            {
                new FilterCondition { Column = "A", Operator = FilterOperator.Eq, Value = "1" },
                new FilterGroup
                {
                    Logic = FilterLogic.Or,
                    Children =
                    {
                        new FilterCondition { Column = "B", Operator = FilterOperator.Eq, Value = "2" },
                        new FilterCondition { Column = "C", Operator = FilterOperator.Eq, Value = "3" },
                    },
                },
            },
        };
        var c = new FilterCompiler(conn).Compile(node);
        var expected =
            $"({Q(conn, "A")} = {P(conn, "f0")} AND " +
            $"({Q(conn, "B")} = {P(conn, "f1")} OR {Q(conn, "C")} = {P(conn, "f2")}))";
        Assert.Equal(expected, c.WhereSql);
        Assert.Equal(3, c.Parameters.Count);
    }

    // Smoke test: Oracle-specific — double-quote identifiers and colon params
    [Fact]
    public void Oracle_DoubleQuotes_And_ColonPrefix()
    {
        var conn = new OracleStubConnector();
        var node = new FilterCondition { Column = "STATUS", Operator = FilterOperator.Eq, Value = "ACTIVE" };
        var c = new FilterCompiler(conn).Compile(node);
        Assert.Equal("\"STATUS\" = :f0", c.WhereSql);
    }

    // Smoke test: SqlServer-specific — square-bracket identifiers and @ params
    [Fact]
    public void SqlServer_BracketQuotes_And_AtPrefix()
    {
        var conn = new SqlServerStub();
        var node = new FilterCondition { Column = "STATUS", Operator = FilterOperator.Eq, Value = "ACTIVE" };
        var c = new FilterCompiler(conn).Compile(node);
        Assert.Equal("[STATUS] = @f0", c.WhereSql);
    }
}
