using System.Text;
using EtlTool.Core.Connectors;
using Microsoft.Data.SqlClient;

namespace EtlTool.Connectors.SqlServer;

/// <summary>
/// Upsert via parameterized MERGE ... USING (VALUES ...). 自動依 SQL Server 2100 參數上限分塊。
/// 對 PK 欄位可走 索引；對非 PK 欄位以 UPDATE/INSERT 處理。
/// </summary>
internal sealed class SqlServerUpsertWriter : IUpsertWriter
{
    private const int MaxParameters = 2000; // SQL Server 上限 2100，留 buffer

    private readonly SqlConnection _conn;
    private readonly SqlTransaction _tx;
    private readonly SqlServerConnector _owner;

    public SqlServerUpsertWriter(SqlConnection conn, SqlTransaction tx, SqlServerConnector owner)
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

        int colCount = targetColumns.Count;
        if (colCount == 0) return 0;

        // 每行需要 colCount 個參數；計算每個 chunk 最多多少行
        int chunkSize = Math.Max(1, MaxParameters / colCount);

        int totalAffected = 0;
        for (int offset = 0; offset < rows.Count; offset += chunkSize)
        {
            int len = Math.Min(chunkSize, rows.Count - offset);
            totalAffected += await MergeChunkAsync(
                schema, table, targetColumns, keyColumns,
                rows, offset, len, ct);
        }
        return totalAffected;
    }

    private async Task<int> MergeChunkAsync(
        string schema,
        string table,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<object?[]> rows,
        int offset,
        int count,
        CancellationToken ct)
    {
        var qualified = _owner.QuoteQualified(schema, table);
        var keySet = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
        var nonKeyCols = targetColumns.Where(c => !keySet.Contains(c)).ToList();

        var sb = new StringBuilder();
        sb.Append("MERGE ").Append(qualified).Append(" AS T USING (VALUES ");

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        for (int r = 0; r < count; r++)
        {
            if (r > 0) sb.Append(',');
            sb.Append('(');
            for (int c = 0; c < targetColumns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                var pname = $"@p{r}_{c}";
                sb.Append(pname);
                cmd.Parameters.Add(new SqlParameter(pname, rows[offset + r][c] ?? DBNull.Value));
            }
            sb.Append(')');
        }

        sb.Append(") AS S(");
        sb.Append(string.Join(',', targetColumns.Select(_owner.QuoteIdentifier)));
        sb.Append(") ON ");
        sb.Append(string.Join(" AND ", keyColumns.Select(k =>
            $"T.{_owner.QuoteIdentifier(k)} = S.{_owner.QuoteIdentifier(k)}")));

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
        sb.Append(");");

        cmd.CommandText = sb.ToString();
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
