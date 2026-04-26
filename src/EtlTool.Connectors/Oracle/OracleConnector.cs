using System.Data.Common;
using EtlTool.Core.Connectors;
using EtlTool.Core.Models;
using Oracle.ManagedDataAccess.Client;

namespace EtlTool.Connectors.Oracle;

public sealed class OracleConnector : IDbConnector
{
    private readonly string _connectionString;
    public DbProviderType Provider => DbProviderType.Oracle;
    public string ParameterPrefix => ":";

    public OracleConnector(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new OracleConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM DUAL";
            var v = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(v) == 1;
        }
        catch
        {
            return false;
        }
    }

    public string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public string QuoteQualified(string schema, string table)
        => string.IsNullOrEmpty(schema) ? QuoteIdentifier(table) : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    public DbParameter CreateParameter(string name, object? value)
        => new OracleParameter(name.StartsWith(':') ? name[1..] : name, value ?? DBNull.Value);

    public async Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // 過濾 Oracle 內建系統 schema
        cmd.CommandText = """
            SELECT USERNAME FROM ALL_USERS
            WHERE USERNAME NOT IN (
                'SYS','SYSTEM','OUTLN','DBSNMP','APPQOSSYS','GSMADMIN_INTERNAL','XDB',
                'WMSYS','CTXSYS','MDSYS','OLAPSYS','LBACSYS','ORDSYS','ORDDATA','ORDPLUGINS',
                'OJVMSYS','XS$NULL','GSMUSER','GSMCATUSER','MDDATA','ANONYMOUS','APEX_PUBLIC_USER',
                'FLOWS_FILES','SYSBACKUP','SYSDG','SYSKM','SYSRAC','REMOTE_SCHEDULER_AGENT',
                'AUDSYS','DVSYS','DVF','DIP'
            )
            AND USERNAME NOT LIKE 'APEX_%' AND USERNAME NOT LIKE 'ORACLE_%' AND USERNAME NOT LIKE 'XS$%'
            ORDER BY USERNAME
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
            SELECT TABLE_NAME FROM ALL_TABLES
            WHERE OWNER = :owner
            ORDER BY TABLE_NAME
            """;
        cmd.Parameters.Add(new OracleParameter("owner", schema));
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
              c.COLUMN_NAME,
              c.DATA_TYPE,
              c.NULLABLE,
              c.DATA_LENGTH,
              c.DATA_PRECISION,
              c.DATA_SCALE,
              CASE WHEN pk.COLUMN_NAME IS NULL THEN 'N' ELSE 'Y' END AS IS_PK
            FROM ALL_TAB_COLUMNS c
            LEFT JOIN (
                SELECT cc.COLUMN_NAME, cc.OWNER, cc.TABLE_NAME
                FROM ALL_CONSTRAINTS cn
                JOIN ALL_CONS_COLUMNS cc
                  ON cc.CONSTRAINT_NAME = cn.CONSTRAINT_NAME AND cc.OWNER = cn.OWNER
                WHERE cn.CONSTRAINT_TYPE = 'P'
            ) pk ON pk.OWNER = c.OWNER AND pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE c.OWNER = :owner AND c.TABLE_NAME = :tname
            ORDER BY c.COLUMN_ID
            """;
        cmd.Parameters.Add(new OracleParameter("owner", schema));
        cmd.Parameters.Add(new OracleParameter("tname", table));

        var list = new List<ColumnInfo>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new ColumnInfo(
                Name: rdr.GetString(0),
                DataType: rdr.GetString(1),
                Nullable: rdr.GetString(2) == "Y",
                IsPrimaryKey: rdr.GetString(6) == "Y",
                MaxLength: rdr.IsDBNull(3) ? null : Convert.ToInt32(rdr.GetValue(3)),
                Precision: rdr.IsDBNull(4) ? null : Convert.ToInt32(rdr.GetValue(4)),
                Scale: rdr.IsDBNull(5) ? null : Convert.ToInt32(rdr.GetValue(5))));
        }
        return list;
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(string schema, string table, CancellationToken ct)
    {
        var cols = await ListColumnsAsync(schema, table, ct);
        return cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
    }

    public IBulkWriter CreateBulkWriter(DbConnection connection, DbTransaction transaction)
        => new OracleBulkWriter((OracleConnection)connection, (OracleTransaction)transaction, this);

    public IUpsertWriter CreateUpsertWriter(DbConnection connection, DbTransaction transaction)
        => new OracleUpsertWriter((OracleConnection)connection, (OracleTransaction)transaction, this);

    public string LimitedSelect(string columnList, string fromQualified, string? whereClause, int limit)
    {
        var w = string.IsNullOrEmpty(whereClause) ? "" : " WHERE " + whereClause;
        return $"SELECT {columnList} FROM {fromQualified}{w} FETCH FIRST {limit} ROWS ONLY";
    }
}
