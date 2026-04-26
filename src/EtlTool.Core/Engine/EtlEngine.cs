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
    private readonly IFailureNotifier? _failureNotifier;

    public EtlEngine(
        ILogger<EtlEngine> log,
        IDbConnectorFactory factory,
        IRunHistorySink runSink,
        IConnectionLookup connectionLookup,
        IAuditLogger? audit = null,
        IFailureNotifier? failureNotifier = null)
    {
        _log = log;
        _factory = factory;
        _runSink = runSink;
        _connectionLookup = connectionLookup;
        _audit = audit;
        _failureNotifier = failureNotifier;
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

            // Schema drift 預檢（在開 transaction 之前；fail 時直接結束 run）
            await PreflightSchemaDriftAsync(task, sourceConnector, targetConnector, run, ct);
            if (run.Status == RunStatus.Failed)
            {
                run.FinishedAt = DateTime.UtcNow;
                await _runSink.PersistAsync(run, ct);
                return run;
            }

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
                // 落地兩個版本：parameterized 原型 + 實際代入值版本（給人複製到 SSMS / SQL Developer 重跑）
                var readRendered = SqlRenderer.Render(readSql, readParams, sourceConnector.Provider, sourceConnector.ParameterPrefix);
                run.GeneratedReadSql = readParams.Count > 0
                    ? $"-- 已展開參數的可執行版本：\n{readRendered}\n\n-- 原始參數化版本：\n{readSql}"
                    : readSql;

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
                            sample[targetCols[i]] = task.MaskSamplePayload ? MaskValue(row[i]) : row[i];
                        samplePayload.Add(sample);
                    }

                    if (batch.Count >= task.BatchSize)
                        await FlushAsync();
                }
                await FlushAsync();

                // Row count assertion — commit 之前檢查；Fail → throw 觸發 rollback
                var rowCheck = RowCountAssertion.Check(rowsRead, task.MinExpectedRows, task.MaxExpectedRows, task.RowCountPolicy);
                if (!rowCheck.Passed && task.RowCountPolicy == RowCountAssertionPolicy.Fail)
                {
                    throw new InvalidOperationException($"Row count assertion 失敗（policy=Fail）：{rowCheck.Violation}");
                }

                await tx.CommitAsync(ct);

                run.RowsRead = rowsRead;
                run.RowsWritten = rowsWritten;

                // Warn 模式違反 → commit 後 audit warning（Fail 模式已透過 throw 走例外路徑）
                if (!rowCheck.Passed && task.RowCountPolicy == RowCountAssertionPolicy.Warn && _audit is not null)
                {
                    await _audit.LogAsync(AuditCategory.Run, AuditAction.RunSucceeded,
                        $"⚠ 任務「{task.Name}」row count 警告：{rowCheck.Violation}（policy=Warn 仍 commit）",
                        targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                        severity: AuditSeverity.Warning, ct: ct);
                }
                run.SamplePayloadJson = JsonSerializer.Serialize(samplePayload);
                run.Status = RunStatus.Success;
                run.FinishedAt = DateTime.UtcNow;
                await _runSink.PersistAsync(run, ct);

                // Post-success SP（commit 之後才呼叫；失敗只 log，不影響 ETL 已成功狀態）
                if (!string.IsNullOrWhiteSpace(task.PostSuccessSp))
                {
                    await InvokePostRunSpAsync(targetConnector, tgtConn, task, task.PostSuccessSp!, run, ct);
                }

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

            // Post-failure SP（best-effort，需重新建立連線因為原來那條已 rollback / dispose）
            if (!string.IsNullOrWhiteSpace(task.PostFailureSp))
            {
                try
                {
                    var failConnDef = await _connectionLookup.GetAsync(task.TargetConnectionId, CancellationToken.None);
                    if (failConnDef is not null)
                    {
                        var failConnector = _factory.Create(failConnDef);
                        await using var failConn = await failConnector.OpenAsync(CancellationToken.None);
                        await InvokePostRunSpAsync(failConnector, failConn, task, task.PostFailureSp!, run, CancellationToken.None);
                    }
                }
                catch (Exception spEx)
                {
                    _log.LogError(spEx, "Post-failure SP {Sp} for task {TaskName} threw", task.PostFailureSp, task.Name);
                }
            }
            if (_audit is not null)
                await _audit.LogAsync(AuditCategory.Run, AuditAction.RunFailed,
                    $"任務「{task.Name}」執行失敗：{ex.Message}",
                    targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                    severity: AuditSeverity.Error,
                    detailsJson: System.Text.Json.JsonSerializer.Serialize(new { runId = run.Id, error = ex.GetType().Name }),
                    ct: CancellationToken.None);

            // Failure webhook（fire-and-forget；webhook 自身錯誤已在 notifier 內 swallow）
            if (_failureNotifier is not null)
            {
                _ = Task.Run(() => _failureNotifier.NotifyFailureAsync(task, run, CancellationToken.None));
            }

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
            // Raw SQL 內的 ${TOKEN} 用 provider-specific date literal 替換
            var substituted = DateTokenResolver.SubstituteRaw(task.FilterRawSql!, connector.Provider);
            sb.Append(" WHERE ").Append(substituted);
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
                sb.Append(" WHERE ").Append(DateTokenResolver.SubstituteRaw(task.FilterRawSql!, connector.Provider));
            }
        }
        else if (!string.IsNullOrWhiteSpace(task.DeleteWhereRawSql))
        {
            sb.Append(" WHERE ").Append(DateTokenResolver.SubstituteRaw(task.DeleteWhereRawSql!, connector.Provider));
        }

        var sql = sb.ToString();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var p in ps) cmd.Parameters.Add(connector.CreateParameter(p.Item1, p.Item2));
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        var rendered = SqlRenderer.Render(sql, ps, connector.Provider, connector.ParameterPrefix);
        return ps.Count > 0
            ? $"-- Delete affected {affected}\n-- 已展開參數的可執行版本：\n{rendered}\n\n-- 原始參數化版本：\n{sql}"
            : $"-- Delete affected {affected}\n{sql}";
    }

    private static string AppendSql(string? existing, string add)
        => string.IsNullOrEmpty(existing) ? add : existing + "\n\n" + add;

    /// <summary>
    /// 執行前的 schema drift 檢查。
    /// - Ignore：直接 return
    /// - Warn：對來源/目標各自比對，drift 寫 audit (severity Warning) 後仍繼續執行
    /// - Fail：若 mapping 受影響的 drift 存在 → run.Status = Failed，run.ErrorMessage 列出
    /// </summary>
    private async Task PreflightSchemaDriftAsync(
        EtlTask task, IDbConnector source, IDbConnector target, RunHistory run, CancellationToken ct)
    {
        if (task.SchemaDriftPolicy == SchemaDriftPolicy.Ignore) return;
        if (string.IsNullOrEmpty(task.SourceSchemaSnapshotJson)
            && string.IsNullOrEmpty(task.TargetSchemaSnapshotJson)) return;

        var mappedSrc = task.Mappings.Select(m => m.SourceColumn).Where(c => !string.IsNullOrEmpty(c)).ToList();
        var mappedTgt = task.Mappings.Select(m => m.TargetColumn).Where(c => !string.IsNullOrEmpty(c)).ToList();

        var drifts = new List<(string side, SchemaDriftReport report)>();

        if (!string.IsNullOrEmpty(task.SourceSchemaSnapshotJson))
        {
            try
            {
                var snap = JsonSerializer.Deserialize<List<ColumnInfo>>(task.SourceSchemaSnapshotJson!) ?? new();
                var current = (await source.ListColumnsAsync(task.SourceSchema, task.SourceTable, ct)).ToList();
                var rep = SchemaDriftDetector.Compare(snap, current, mappedSrc);
                if (rep.HasDrift) drifts.Add(("Source", rep));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to compare source schema snapshot for {TaskName}", task.Name);
            }
        }

        if (!string.IsNullOrEmpty(task.TargetSchemaSnapshotJson))
        {
            try
            {
                var snap = JsonSerializer.Deserialize<List<ColumnInfo>>(task.TargetSchemaSnapshotJson!) ?? new();
                var current = (await target.ListColumnsAsync(task.TargetSchema, task.TargetTable, ct)).ToList();
                var rep = SchemaDriftDetector.Compare(snap, current, mappedTgt);
                if (rep.HasDrift) drifts.Add(("Target", rep));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to compare target schema snapshot for {TaskName}", task.Name);
            }
        }

        if (drifts.Count == 0) return;

        var summary = string.Join("；", drifts.Select(d => $"{d.side}: {d.report.ShortSummary()}"));
        var details = JsonSerializer.Serialize(drifts.Select(d => new
        {
            side = d.side,
            summary = d.report.ShortSummary(),
            affectingMapping = d.report.AffectingMappedColumns,
            items = d.report.Items.Select(i => new { i.ColumnName, kind = i.Kind.ToString(), i.Was, i.IsNow }),
        }));

        bool affecting = drifts.Any(d => (d.report.AffectingMappedColumns ?? 0) > 0);

        if (task.SchemaDriftPolicy == SchemaDriftPolicy.Fail && affecting)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = $"Schema drift 偵測中止執行（policy=Fail）：{summary}";
            if (_audit is not null)
                await _audit.LogAsync(AuditCategory.Run, AuditAction.RunFailed,
                    $"任務「{task.Name}」schema drift 觸發 fail-fast：{summary}",
                    targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                    severity: AuditSeverity.Error, detailsJson: details, ct: ct);
            return;
        }

        // Warn (or Fail with no mapping-affecting drift) → 繼續執行但 audit
        if (_audit is not null)
        {
            await _audit.LogAsync(AuditCategory.Run, AuditAction.RunStarted,
                $"⚠ 任務「{task.Name}」偵測到 schema drift（仍會執行）：{summary}",
                targetType: nameof(EtlTask), targetId: task.Id, targetName: task.Name,
                severity: affecting ? AuditSeverity.Warning : AuditSeverity.Info,
                detailsJson: details, ct: ct);
        }
    }

    /// <summary>
    /// PII 遮罩規則：
    ///   - null / 數值 / 布林 / 日期 / byte[] → 不遮罩（型別本身少 PII；遮罩會破壞 schema 直觀性）
    ///   - 字串 ≤ 4 字 → 不遮罩（縮寫、狀態碼、貨幣代號等通常不敏感）
    ///   - 字串 > 4 字 → 保留首尾各 1 字，中間用 * 填滿至原長度
    /// 例：
    ///   "Alice"      → "A***e"
    ///   "Anderson"   → "A******n"
    ///   "0912345678" → "0********8"
    ///   "tw"         → "tw"
    /// </summary>
    public static object? MaskValue(object? value)
    {
        if (value is null) return null;
        if (value is string s)
        {
            if (s.Length <= 4) return s;
            return string.Concat(s[0], new string('*', s.Length - 2), s[^1]);
        }
        return value;
    }

    /// <summary>
    /// 呼叫使用者設定的 stored procedure（在目標 DB 上）。
    /// 標準參數（命名一律小寫底線，無前綴；連接器自動加 ":" 或 "@"）：
    ///   task_id        Guid (string)    任務 ID
    ///   task_name      string           任務名稱
    ///   run_id         Guid (string)    本次 RunHistory ID
    ///   status         string           "Success" / "Failed"
    ///   rows_read      long             讀取筆數
    ///   rows_written   long             寫入筆數
    ///   started_at     DateTime         UTC 開始時間
    ///   finished_at    DateTime         UTC 結束時間
    ///   error_message  string?          錯誤訊息（成功時 null / 空字串）
    ///   trigger_type   string           "Scheduled" / "Manual" / "Retry"
    /// SP 簽章範例（SQL Server）：
    ///   CREATE PROCEDURE dbo.OnEtlCompleted
    ///       @task_id NVARCHAR(50), @task_name NVARCHAR(100),
    ///       @run_id NVARCHAR(50), @status NVARCHAR(16),
    ///       @rows_read BIGINT, @rows_written BIGINT,
    ///       @started_at DATETIME2, @finished_at DATETIME2,
    ///       @error_message NVARCHAR(MAX) = NULL,
    ///       @trigger_type NVARCHAR(16) = NULL
    ///   AS BEGIN ... END
    /// </summary>
    private async Task InvokePostRunSpAsync(
        IDbConnector connector, DbConnection conn,
        EtlTask task, string spName, RunHistory run, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandText = spName;
            cmd.CommandTimeout = 60;

            cmd.Parameters.Add(connector.CreateParameter("task_id", task.Id.ToString()));
            cmd.Parameters.Add(connector.CreateParameter("task_name", task.Name));
            cmd.Parameters.Add(connector.CreateParameter("run_id", run.Id.ToString()));
            cmd.Parameters.Add(connector.CreateParameter("status", run.Status.ToString()));
            cmd.Parameters.Add(connector.CreateParameter("rows_read", run.RowsRead));
            cmd.Parameters.Add(connector.CreateParameter("rows_written", run.RowsWritten));
            cmd.Parameters.Add(connector.CreateParameter("started_at", run.StartedAt));
            cmd.Parameters.Add(connector.CreateParameter("finished_at", run.FinishedAt ?? DateTime.UtcNow));
            cmd.Parameters.Add(connector.CreateParameter("error_message", (object?)run.ErrorMessage ?? DBNull.Value));
            cmd.Parameters.Add(connector.CreateParameter("trigger_type", run.TriggerType.ToString()));

            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("Post-run SP {Sp} executed for task {TaskName} (run {RunId})",
                spName, task.Name, run.Id);
        }
        catch (Exception ex)
        {
            // 不重新拋 — SP 失敗不應影響 ETL 已寫入的資料
            _log.LogError(ex, "Post-run SP {Sp} failed for task {TaskName} (run {RunId})",
                spName, task.Name, run.Id);
            run.ErrorMessage = (run.ErrorMessage ?? "") + $"\n[Post-run SP failed: {ex.Message}]";
            try { await _runSink.PersistAsync(run, CancellationToken.None); } catch { /* swallow */ }
        }
    }
}

public interface IRunHistorySink
{
    Task PersistAsync(RunHistory run, CancellationToken ct);
}

public interface IConnectionLookup
{
    Task<ConnectionDefinition?> GetAsync(Guid id, CancellationToken ct);
}
