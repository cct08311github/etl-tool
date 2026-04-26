using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using EtlTool.Core.Connectors;
using EtlTool.Core.Models;
using Microsoft.Extensions.Logging;

namespace EtlTool.Core.Engine;

public sealed class EtlEngine
{
    private const int SamplePayloadMaxRows = 5;
    private readonly ILogger<EtlEngine> _log;
    private readonly IDbConnectorFactory _factory;
    private readonly IRunHistorySink _runSink;
    private readonly IConnectionLookup _connectionLookup;
    private readonly IAuditLogger? _audit;

    public EtlEngine(
        ILogger<EtlEngine> log,
        IDbConnectorFactory factory,
        IRunHistorySink runSink,
        IConnectionLookup connectionLookup,
        IAuditLogger? audit = null)
    {
        _log = log;
        _factory = factory;
        _runSink = runSink;
        _connectionLookup = connectionLookup;
        _audit = audit;
    }

    /// <summary>
    /// 執行單次 ETL 任務。整段以目標 DB 的單一 transaction 包覆，失敗整個 rollback。
    /// 來源讀取為 streaming（DataReader），不會把所有資料載入記憶體。
    /// </summary>
    public async Task<RunHistory> ExecuteAsync(EtlTask task, TriggerType triggerType, CancellationToken ct)
    {
        var run = new RunHistory
        {
            EtlTaskId = task.Id,
            TriggerType = triggerType,
            StartedAt = DateTime.UtcNow,
            Status = RunStatus.Running,
        };
        await _runSink.PersistAsync(run, ct);

        if (_audit is not null)
            await _audit.LogAsync(AuditCategory.Run, AuditAction.RunStarted,
                $"開始執行任務「{task.Name}」（{triggerType}）",
                targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name, ct: ct);

        try
        {
            var sourceDef = await _connectionLookup.GetAsync(task.SourceConnectionId, ct)
                ?? throw new InvalidOperationException($"Source connection {task.SourceConnectionId} not found.");
            var targetDef = await _connectionLookup.GetAsync(task.TargetConnectionId, ct)
                ?? throw new InvalidOperationException($"Target connection {task.TargetConnectionId} not found.");

            var sourceConnector = _factory.Create(sourceDef);
            var targetConnector = _factory.Create(targetDef);

            await using var srcConn = await sourceConnector.OpenAsync(ct);
            await using var tgtConn = await targetConnector.OpenAsync(ct);

            await using var tx = await tgtConn.BeginTransactionAsync(ct);

            try
            {
                var evaluator = TransformEvaluator.Compile(task.Mappings);

                // 1) DeleteInsert 模式：先刪除
                if (task.WriteMode == WriteMode.DeleteInsert)
                {
                    var deleteSql = await ExecuteDeleteAsync(targetConnector, tgtConn, tx, task, ct);
                    run.GeneratedWriteSql = AppendSql(run.GeneratedWriteSql, deleteSql);
                }

                // 2) 編譯讀取 SQL
                var (readSql, readParams) = BuildReadSql(sourceConnector, task, evaluator);
                run.GeneratedReadSql = readSql + (readParams.Count > 0
                    ? "\n-- params: " + string.Join(", ", readParams.Select(p => $"{p.Name}={p.Value}"))
                    : "");

                // 3) ExecuteReader streaming
                await using var readCmd = srcConn.CreateCommand();
                readCmd.CommandText = readSql;
                foreach (var p in readParams)
                {
                    readCmd.Parameters.Add(sourceConnector.CreateParameter(p.Name, p.Value));
                }
                await using var reader = await readCmd.ExecuteReaderAsync(ct);

                // 4) 寫入策略 + batch loop
                var targetCols = evaluator.Mappings.Select(m => m.TargetColumn).ToList();
                var keyCols = evaluator.Mappings.Where(m => m.IsKey).Select(m => m.TargetColumn).ToList();

                if (task.WriteMode == WriteMode.Upsert && keyCols.Count == 0)
                    throw new InvalidOperationException("Upsert 模式需至少勾選一個主鍵欄位 (IsKey)。");

                var batch = new List<object?[]>(task.BatchSize);
                var samplePayload = new List<Dictionary<string, object?>>();

                long rowsRead = 0;
                long rowsWritten = 0;

                async Task FlushAsync()
                {
                    if (batch.Count == 0) return;

                    int written;
                    if (task.WriteMode == WriteMode.Upsert)
                    {
                        await using var writer = targetConnector.CreateUpsertWriter(tgtConn, tx);
                        written = await writer.UpsertBatchAsync(
                            task.TargetSchema, task.TargetTable,
                            targetCols, keyCols, batch, ct);
                        if (run.GeneratedWriteSql is null)
                            run.GeneratedWriteSql = $"-- Upsert via MERGE (provider={targetConnector.Provider})";
                    }
                    else
                    {
                        await using var writer = targetConnector.CreateBulkWriter(tgtConn, tx);
                        written = await writer.WriteBatchAsync(
                            task.TargetSchema, task.TargetTable,
                            targetCols, batch, ct);
                        if (run.GeneratedWriteSql is null || !run.GeneratedWriteSql.Contains("INSERT INTO"))
                            run.GeneratedWriteSql = AppendSql(run.GeneratedWriteSql,
                                $"-- BulkInsert into {targetConnector.QuoteQualified(task.TargetSchema, task.TargetTable)} ({string.Join(",", targetCols)})");
                    }
                    rowsWritten += written;
                    batch.Clear();
                }

                while (await reader.ReadAsync(ct))
                {
                    rowsRead++;
                    var row = evaluator.Project(reader);
                    batch.Add(row);

                    if (samplePayload.Count < SamplePayloadMaxRows)
                    {
                        var sample = new Dictionary<string, object?>(targetCols.Count);
                        for (int i = 0; i < targetCols.Count; i++)
                            sample[targetCols[i]] = row[i];
                        samplePayload.Add(sample);
                    }

                    if (batch.Count >= task.BatchSize)
                        await FlushAsync();
                }
                await FlushAsync();

                await tx.CommitAsync(ct);

                run.RowsRead = rowsRead;
                run.RowsWritten = rowsWritten;
                run.SamplePayloadJson = JsonSerializer.Serialize(samplePayload);
                run.Status = RunStatus.Success;
                run.FinishedAt = DateTime.UtcNow;
                await _runSink.PersistAsync(run, ct);

                _log.LogInformation("ETL {TaskName} ({TaskId}) succeeded: read={Read} written={Written}",
                    task.Name, task.Id, rowsRead, rowsWritten);
                if (_audit is not null)
                    await _audit.LogAsync(AuditCategory.Run, AuditAction.RunSucceeded,
                        $"任務「{task.Name}」執行成功（讀 {rowsRead} 筆，寫 {rowsWritten} 筆）",
                        targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                        detailsJson: System.Text.Json.JsonSerializer.Serialize(new { runId = run.Id, rowsRead, rowsWritten }),
                        ct: CancellationToken.None);
                return run;
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { /* swallow rollback errors */ }
                throw;
            }
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.FinishedAt = DateTime.UtcNow;
            run.ErrorMessage = ex.ToString();
            await _runSink.PersistAsync(run, CancellationToken.None);
            _log.LogError(ex, "ETL {TaskName} ({TaskId}) failed", task.Name, task.Id);
            if (_audit is not null)
                await _audit.LogAsync(AuditCategory.Run, AuditAction.RunFailed,
                    $"任務「{task.Name}」執行失敗：{ex.Message}",
                    targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                    severity: AuditSeverity.Error,
                    detailsJson: System.Text.Json.JsonSerializer.Serialize(new { runId = run.Id, error = ex.GetType().Name }),
                    ct: CancellationToken.None);
            return run;
        }
    }

    /// <summary>
    /// 對來源跑限筆數的 SELECT，不寫入目標、不開 transaction、不記 RunHistory。
    /// 給 UI 的 dry-run 預覽用。
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> PreviewSourceAsync(EtlTask task, int limit, CancellationToken ct)
    {
        var srcDef = await _connectionLookup.GetAsync(task.SourceConnectionId, ct)
            ?? throw new InvalidOperationException($"Source connection {task.SourceConnectionId} not found.");
        var connector = _factory.Create(srcDef);

        var cols = task.Mappings
            .Select(m => m.SourceColumn)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();
        if (cols.Count == 0)
        {
            // 還沒設好 mapping —— 用 * 也不安全，改抓所有欄位
            await using var c2 = await connector.OpenAsync(ct);
            var all = await connector.ListColumnsAsync(task.SourceSchema, task.SourceTable, ct);
            cols = all.Select(x => x.Name).ToList();
        }

        var quotedCols = string.Join(',', cols.Select(connector.QuoteIdentifier));
        var qualified = connector.QuoteQualified(task.SourceSchema, task.SourceTable);

        string? whereSql = null;
        IReadOnlyList<(string Name, object? Value)> ps = Array.Empty<(string, object?)>();
        if (task.FilterMode == FilterMode.FormBuilder && !string.IsNullOrWhiteSpace(task.FilterFormJson))
        {
            var node = FilterTreeJson.Deserialize(task.FilterFormJson!);
            var compiled = new FilterCompiler(connector).Compile(node);
            whereSql = compiled.WhereSql;
            ps = compiled.Parameters;
        }
        else if (task.FilterMode == FilterMode.RawSql && !string.IsNullOrWhiteSpace(task.FilterRawSql))
        {
            whereSql = task.FilterRawSql;
        }

        var sql = connector.LimitedSelect(quotedCols, qualified, whereSql, limit);

        await using var conn = await connector.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.Add(connector.CreateParameter(p.Name, p.Value));

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await rdr.ReadAsync(ct))
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < rdr.FieldCount; i++)
                dict[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
            rows.Add(dict);
        }
        return rows;
    }

    private static (string Sql, IReadOnlyList<(string Name, object? Value)> Params) BuildReadSql(
        IDbConnector connector, EtlTask task, TransformEvaluator evaluator)
    {
        var sourceCols = evaluator.Mappings.Select(m => m.SourceColumn).Distinct().ToList();

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        sb.Append(string.Join(',', sourceCols.Select(connector.QuoteIdentifier)));
        sb.Append(" FROM ");
        sb.Append(connector.QuoteQualified(task.SourceSchema, task.SourceTable));

        IReadOnlyList<(string, object?)> ps = Array.Empty<(string, object?)>();
        if (task.FilterMode == FilterMode.FormBuilder && !string.IsNullOrWhiteSpace(task.FilterFormJson))
        {
            var node = FilterTreeJson.Deserialize(task.FilterFormJson!);
            var compiled = new FilterCompiler(connector).Compile(node);
            if (!string.IsNullOrEmpty(compiled.WhereSql))
            {
                sb.Append(" WHERE ").Append(compiled.WhereSql);
                ps = compiled.Parameters;
            }
        }
        else if (task.FilterMode == FilterMode.RawSql && !string.IsNullOrWhiteSpace(task.FilterRawSql))
        {
            sb.Append(" WHERE ").Append(task.FilterRawSql);
        }

        return (sb.ToString(), ps);
    }

    private static async Task<string> ExecuteDeleteAsync(
        IDbConnector connector, DbConnection conn, DbTransaction tx, EtlTask task, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("DELETE FROM ").Append(connector.QuoteQualified(task.TargetSchema, task.TargetTable));

        IReadOnlyList<(string, object?)> ps = Array.Empty<(string, object?)>();

        if (task.DeleteWhereSameAsFilter)
        {
            if (task.FilterMode == FilterMode.FormBuilder && !string.IsNullOrWhiteSpace(task.FilterFormJson))
            {
                var node = FilterTreeJson.Deserialize(task.FilterFormJson!);
                var compiled = new FilterCompiler(connector).Compile(node);
                if (!string.IsNullOrEmpty(compiled.WhereSql))
                {
                    sb.Append(" WHERE ").Append(compiled.WhereSql);
                    ps = compiled.Parameters;
                }
            }
            else if (task.FilterMode == FilterMode.RawSql && !string.IsNullOrWhiteSpace(task.FilterRawSql))
            {
                sb.Append(" WHERE ").Append(task.FilterRawSql);
            }
        }
        else if (!string.IsNullOrWhiteSpace(task.DeleteWhereRawSql))
        {
            sb.Append(" WHERE ").Append(task.DeleteWhereRawSql);
        }

        var sql = sb.ToString();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.Add(connector.CreateParameter(p.Item1, p.Item2));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return $"-- Delete affected {affected}\n{sql}";
    }

    private static string AppendSql(string? existing, string add)
        => string.IsNullOrEmpty(existing) ? add : existing + "\n\n" + add;
}

public interface IRunHistorySink
{
    Task PersistAsync(RunHistory run, CancellationToken ct);
}

public interface IConnectionLookup
{
    Task<ConnectionDefinition?> GetAsync(Guid id, CancellationToken ct);
}
