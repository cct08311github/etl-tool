using System.Data;
using EtlTool.Core.Connectors;
using Microsoft.Data.SqlClient;

namespace EtlTool.Connectors.SqlServer;

internal sealed class SqlServerBulkWriter : IBulkWriter
{
    private readonly SqlConnection _conn;
    private readonly SqlTransaction _tx;

    public SqlServerBulkWriter(SqlConnection conn, SqlTransaction tx)
    {
        _conn = conn;
        _tx = tx;
    }

    public async Task<int> WriteBatchAsync(
        string schema,
        string table,
        IReadOnlyList<string> targetColumns,
        IReadOnlyList<object?[]> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return 0;

        using var bulk = new SqlBulkCopy(_conn, SqlBulkCopyOptions.Default, _tx)
        {
            DestinationTableName = string.IsNullOrEmpty(schema) ? $"[{table}]" : $"[{schema}].[{table}]",
            BatchSize = rows.Count,
            EnableStreaming = true,
        };
        for (int i = 0; i < targetColumns.Count; i++)
        {
            bulk.ColumnMappings.Add(i, targetColumns[i]);
        }

        using var dt = new DataTable();
        for (int i = 0; i < targetColumns.Count; i++)
            dt.Columns.Add(targetColumns[i], typeof(object));

        foreach (var row in rows)
        {
            var arr = new object[row.Length];
            for (int i = 0; i < row.Length; i++) arr[i] = row[i] ?? DBNull.Value;
            dt.Rows.Add(arr);
        }

        await bulk.WriteToServerAsync(dt, ct);
        return rows.Count;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
