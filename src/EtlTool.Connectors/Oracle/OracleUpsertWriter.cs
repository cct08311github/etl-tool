using System.Text;
using EtlTool.Core.Connectors;
using Oracle.ManagedDataAccess.Client;

namespace EtlTool.Connectors.Oracle;

/// <summary>
/// 用 MERGE INTO + array binding 做 upsert。
/// 單一 SQL 內嵌 :p0, :p1... 對應每欄；ArrayBindCount = batch.Count。
/// </summary>
internal sealed class OracleUpsertWriter : IUpsertWriter
{
    private readonly OracleConnection _conn;
    private readonly OracleTransaction _tx;
    private readonly OracleConnector _owner;

    public OracleUpsertWriter(OracleConnection conn, OracleTransaction tx, OracleConnector owner)
    {
        _conn = conn;
        _tx = tx;
        _owner = owner;
    }

    public async Task<int> UpsertBatchAsync(
        string schema,
        string table,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<object?[]> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        if (keyColumns.Count == 0) throw new InvalidOperationException("Upsert requires at least one key column.");

        var qualified = _owner.QuoteQualified(schema, table);
        var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var nonKeyCols = targetColumns.Where(c => !keySet.Contains(c)).ToList();

        var sb = new StringBuilder();
        sb.Append("MERGE INTO ").Append(qualified).Append(" T USING (SELECT ");
        for (int i = 0; i < targetColumns.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(":p").Append(i).Append(" AS ").Append(_owner.QuoteIdentifier(targetColumns[i]));
        }
        sb.Append(" FROM DUAL) S ON (");
        sb.Append(string.Join(" AND ", keyColumns.Select(k =>
            $"T.{_owner.QuoteIdentifier(k)} = S.{_owner.QuoteIdentifier(k)}")));
        sb.Append(')');

        if (nonKeyCols.Count > 0)
        {
            sb.Append(" WHEN MATCHED THEN UPDATE SET ");
            sb.Append(string.Join(',', nonKeyCols.Select(c =>
                $"T.{_owner.QuoteIdentifier(c)} = S.{_owner.QuoteIdentifier(c)}")));
        }

        sb.Append(" WHEN NOT MATCHED THEN INSERT (");
        sb.Append(string.Join(',', targetColumns.Select(_owner.QuoteIdentifier)));
        sb.Append(") VALUES (");
        sb.Append(string.Join(',', targetColumns.Select(c => $"S.{_owner.QuoteIdentifier(c)}")));
        sb.Append(')');

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sb.ToString();
        cmd.BindByName = true;
        cmd.ArrayBindCount = rows.Count;

        for (int c = 0; c < targetColumns.Count; c++)
        {
            var arr = new object?[rows.Count];
            for (int r = 0; r < rows.Count; r++)
                arr[r] = rows[r][c] ?? DBNull.Value;
            var dbType = OracleBulkWriter.InferOracleDbType(arr);
            var p = new OracleParameter($"p{c}", dbType) { Value = arr };
            cmd.Parameters.Add(p);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
