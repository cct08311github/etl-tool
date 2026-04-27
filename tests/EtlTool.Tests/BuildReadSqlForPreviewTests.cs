using System.Data.Common;
using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

/// <summary>
/// 確認 EtlEngine.BuildReadSqlForPreview 對 in-memory（未存檔的）EtlTask 也能正確產生 SQL，
/// 並能搭配 form-builder filter / raw SQL filter / 無 filter 三種狀態。
/// 不依賴 DB；用 stub connector。
/// </summary>
public class BuildReadSqlForPreviewTests
{
    private static EtlTask MakeTask(string schema, string table, params (string src, string tgt)[] cols)
    {
        var t = new EtlTask
        {
            SourceSchema = schema,
            SourceTable = table,
            TargetSchema = "dbo",
            TargetTable = "Out",
        };
        foreach (var (src, tgt) in cols)
            t.Mappings.Add(new ColumnMapping { SourceColumn = src, TargetColumn = tgt });
        return t;
    }

    [Fact]
    public void Throws_when_no_mappings()
    {
        var t = new EtlTask { SourceSchema = "dbo", SourceTable = "X" };
        Assert.Throws<InvalidOperationException>(
            () => EtlEngine.BuildReadSqlForPreview(new SqlServerStub(), t));
    }

    [Fact]
    public void No_filter_emits_plain_select()
    {
        var t = MakeTask("dbo", "Customers", ("Name", "Name"), ("Email", "Email"));
        var (sql, ps) = EtlEngine.BuildReadSqlForPreview(new SqlServerStub(), t);
        Assert.Equal("SELECT [Name],[Email] FROM [dbo].[Customers]", sql);
        Assert.Empty(ps);
    }

    [Fact]
    public void Form_filter_compiles_into_where_with_params()
    {
        var t = MakeTask("dbo", "Customers", ("Id", "Id"));
        t.FilterMode = FilterMode.FormBuilder;
        t.FilterFormJson = """{"kind":"group","op":"AND","children":[{"kind":"condition","column":"Status","operator":"Eq","value":"active"}]}""";

        var (sql, ps) = EtlEngine.BuildReadSqlForPreview(new SqlServerStub(), t);
        Assert.Contains("WHERE", sql);
        Assert.Contains("[Status] = @f0", sql);
        Assert.Single(ps);
        Assert.Equal("active", ps[0].Value);
    }

    [Fact]
    public void Raw_sql_filter_appended_verbatim()
    {
        var t = MakeTask("dbo", "Customers", ("Id", "Id"));
        t.FilterMode = FilterMode.RawSql;
        t.FilterRawSql = "[Status] IN ('a', 'b')";
        var (sql, ps) = EtlEngine.BuildReadSqlForPreview(new SqlServerStub(), t);
        Assert.EndsWith("WHERE [Status] IN ('a', 'b')", sql);
        Assert.Empty(ps);
    }

    [Fact]
    public void Distinct_columns_when_mapping_has_duplicates()
    {
        // 同一個 source 欄位可被映射到多個 target 欄位（理論上少見但合法），
        // 預覽 SQL 不該重複 SELECT 該欄位。
        var t = MakeTask("dbo", "T", ("A", "X1"), ("A", "X2"), ("B", "Y"));
        var (sql, _) = EtlEngine.BuildReadSqlForPreview(new SqlServerStub(), t);
        Assert.Equal("SELECT [A],[B] FROM [dbo].[T]", sql);
    }

    [Fact]
    public void Oracle_dialect_uses_double_quotes_and_colon_params()
    {
        var t = MakeTask("HR", "EMPLOYEES", ("EMP_ID", "EmpId"));
        t.FilterMode = FilterMode.FormBuilder;
        t.FilterFormJson = """{"kind":"group","op":"AND","children":[{"kind":"condition","column":"DEPT","operator":"Eq","value":"R&D"}]}""";

        var (sql, ps) = EtlEngine.BuildReadSqlForPreview(new OracleStub(), t);
        Assert.Contains("\"EMP_ID\"", sql);
        Assert.Contains("\"HR\".\"EMPLOYEES\"", sql);
        Assert.Contains("\"DEPT\" = :f0", sql);
        Assert.Single(ps);
    }

    // ── Stub connectors (minimal — only what BuildReadSql needs) ─────────────
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

    private sealed class OracleStub : IDbConnector
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
}
