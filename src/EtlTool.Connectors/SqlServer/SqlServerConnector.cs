using System.Data;
using System.Data.Common;
using EtlTool.Core.Connectors;
using EtlTool.Core.Models;
using Microsoft.Data.SqlClient;

namespace EtlTool.Connectors.SqlServer;

public sealed class SqlServerConnector : IDbConnector
{
    private readonly string _connectionString;
    public DbProviderType Provider => DbProviderType.SqlServer;
    public string ParameterPrefix => "@";

    public SqlServerConnector(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(v) == 1;
        }
        catch
        {
            return false;
        }
    }

    public string QuoteIdentifier(string name) => $"[{name.Replace("]", "]]")}]";

    public string QuoteQualified(string schema, string table)
        => string.IsNullOrEmpty(schema) ? QuoteIdentifier(table) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    public DbParameter CreateParameter(string name, object? value)
        => new SqlParameter(name.StartsWith('@') ? name : "@" + name, value ?? DBNull.Value);

    public async Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM sys.schemas
            WHERE name NOT IN ('guest','INFORMATION_SCHEMA','sys','db_owner','db_accessadmin','db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter','db_denydatareader','db_denydatawriter')
            ORDER BY name
            """;
        var list = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) list.Add(rdr.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(string schema, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema
            ORDER BY t.name
            """;
        cmd.Parameters.Add(new SqlParameter("@schema", schema));
        var list = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) list.Add(rdr.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string schema, string table, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              c.name,
              ty.name AS data_type,
              c.is_nullable,
              c.max_length,
              c.precision,
              c.scale,
              CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END AS is_pk
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE s.name = @schema AND t.name = @table
            ORDER BY c.column_id
            """;
        cmd.Parameters.Add(new SqlParameter("@schema", schema));
        cmd.Parameters.Add(new SqlParameter("@table", table));
        var list = new List<ColumnInfo>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new ColumnInfo(
                Name: rdr.GetString(0),
                DataType: rdr.GetString(1),
                Nullable: rdr.GetBoolean(2),
                IsPrimaryKey: rdr.GetInt32(6) == 1,
                MaxLength: rdr.GetInt16(3),
                Precision: rdr.GetByte(4),
                Scale: rdr.GetByte(5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(string schema, string table, CancellationToken ct)
    {
        var cols = await ListColumnsAsync(schema, table, ct);
        return cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
    }

    public IBulkWriter CreateBulkWriter(DbConnection connection, DbTransaction transaction)
        => new SqlServerBulkWriter((SqlConnection)connection, (SqlTransaction)transaction);

    public IUpsertWriter CreateUpsertWriter(DbConnection connection, DbTransaction transaction)
        => new SqlServerUpsertWriter((SqlConnection)connection, (SqlTransaction)transaction, this);

    public string LimitedSelect(string columnList, string fromQualified, string? whereClause, int limit)
    {
        var w = string.IsNullOrEmpty(whereClause) ? "" : " WHERE " + whereClause;
        return $"SELECT TOP {limit} {columnList} FROM {fromQualified}{w}";
    }
}
