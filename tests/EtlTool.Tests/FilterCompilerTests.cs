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
