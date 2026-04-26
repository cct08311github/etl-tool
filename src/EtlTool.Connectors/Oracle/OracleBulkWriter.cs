using EtlTool.Core.Connectors;
using Oracle.ManagedDataAccess.Client;

namespace EtlTool.Connectors.Oracle;

/// <summary>
/// 用 OracleParameter array binding 做高效批次 INSERT。
/// 一次 OracleCommand.ExecuteNonQuery 等於執行 ArrayBindCount 次語句。
/// </summary>
internal sealed class OracleBulkWriter : IBulkWriter
{
    private readonly OracleConnection _conn;
    private readonly OracleTransaction _tx;
    private readonly OracleConnector _owner;

    public OracleBulkWriter(OracleConnection conn, OracleTransaction tx, OracleConnector owner)
    {
        _conn = conn;
        _tx = tx;
        _owner = owner;
    }

    public async Task<int> WriteBatchAsync(
        string schema,
        string table,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<object?[]> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        var qualified = _owner.QuoteQualified(schema, table);
        var colList = string.Join(',', targetColumns.Select(_owner.QuoteIdentifier));
        var paramList = string.Join(',', targetColumns.Select((_, i) => $":p{i}"));

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = $"INSERT INTO {qualified} ({colList}) VALUES ({paramList})";
        cmd.BindByName = true;
        cmd.ArrayBindCount = rows.Count;

        for (int c = 0; c < targetColumns.Count; c++)
        {
            var arr = new object?[rows.Count];
            for (int r = 0; r < rows.Count; r++)
                arr[r] = rows[r][c] ?? DBNull.Value;

            var dbType = InferOracleDbType(arr);
            var p = new OracleParameter($"p{c}", dbType) { Value = arr };
            cmd.Parameters.Add(p);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>從 batch 的第一個非 DBNull 值推斷 OracleDbType；全 null 時用 Varchar2。</summary>
    internal static OracleDbType InferOracleDbType(object?[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            var v = arr[i];
            if (v is null or DBNull) continue;
            return v switch
            {
                bool => OracleDbType.Byte,
                byte => OracleDbType.Byte,
                short => OracleDbType.Int16,
                int => OracleDbType.Int32,
                long => OracleDbType.Int64,
                float => OracleDbType.Single,
                double => OracleDbType.Double,
                decimal => OracleDbType.Decimal,
                Guid => OracleDbType.Varchar2,
                DateTime => OracleDbType.Date,
                DateTimeOffset => OracleDbType.TimeStampTZ,
                byte[] => OracleDbType.Blob,
                string => OracleDbType.Varchar2,
                _ => OracleDbType.Varchar2,
            };
        }
        return OracleDbType.Varchar2;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
