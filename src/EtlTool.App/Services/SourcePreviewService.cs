using System.Data.Common;
using EtlTool.Core.Connectors;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.App.Services;

/// <summary>
/// 給 TaskEdit「預覽前 N 筆來源資料」按鈕用的服務。
///
/// 流程：
///   1. 拿目前 in-memory（可能未存檔）的 EtlTask
///   2. 用 EtlEngine.BuildReadSqlForPreview 產生 read SQL（套用 filter）
///   3. 依 provider 在 SQL 外面包一層 row limiter（SQL Server: TOP / Oracle: ROWNUM）
///   4. Open source connection，執行 → 拉前 N 筆 + column 名稱 + 欄位類型
///   5. 自動套 PiiDetector 對偵測到的 PII 欄位做 mask（不論 task.MaskSamplePayload）
///      — 預覽永遠安全，避免把客戶機敏資料貼到截圖 / 對話訊息
///
/// 5 秒 timeout 防止意外長查詢拖死 UI。
/// </summary>
public sealed class SourcePreviewService
{
    private readonly IDbConnectorFactory _connectorFactory;
    private readonly EtlTool.Data.Repositories.ConnectionRepository _connRepo;

    public SourcePreviewService(
        IDbConnectorFactory connectorFactory,
        EtlTool.Data.Repositories.ConnectionRepository connRepo)
    {
        _connectorFactory = connectorFactory;
        _connRepo = connRepo;
    }

    public sealed record PreviewResult(
        bool Ok,
        string? Sql,
        IReadOnlyList<string> Columns,
        IReadOnlyList<Dictionary<string, object?>> Rows,
        IReadOnlyList<string> MaskedColumns,
        long ElapsedMs,
        string? Error);

    public async Task<PreviewResult> PreviewAsync(EtlTask task, int rowLimit, CancellationToken ct)
    {
        if (rowLimit <= 0 || rowLimit > 100) rowLimit = 10;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 連線存在嗎？（task.SourceConnectionId 是 in-memory 值，可能為 Empty）
            if (task.SourceConnectionId == Guid.Empty)
                return Fail(sw, "尚未選擇來源連線。");
            if (string.IsNullOrEmpty(task.SourceTable))
                return Fail(sw, "尚未選擇來源資料表。");
            var conn = await _connRepo.GetAsync(task.SourceConnectionId, ct)
                ?? throw new InvalidOperationException($"找不到連線 ID {task.SourceConnectionId}（可能已被刪除）。");

            var connector = _connectorFactory.Create(conn);
            var (innerSql, parameters) = EtlEngine.BuildReadSqlForPreview(connector, task);
            var pagedSql = WrapWithRowLimit(connector.Provider, innerSql, rowLimit);

            // 執行
            await using var dbConn = await connector.OpenAsync(ct);
            await using var cmd = dbConn.CreateCommand();
            cmd.CommandText = pagedSql;
            cmd.CommandTimeout = 5;  // 銀行客戶 prod source 可能很大，5 秒夠看 TOP 10
            foreach (var (name, value) in parameters)
                cmd.Parameters.Add(connector.CreateParameter(name, value));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var columns = new List<string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));

            // PII auto-mask（同 EtlEngine 的 sample payload 邏輯，安全預設）
            var piiKindByCol = new Dictionary<string, PiiDetector.PiiKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in columns)
                piiKindByCol[c] = PiiDetector.Inspect(c).Kind;
            var maskedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(ct) && rows.Count < rowLimit)
            {
                var row = new Dictionary<string, object?>(columns.Count);
                for (int i = 0; i < columns.Count; i++)
                {
                    var col = columns[i];
                    var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    if (piiKindByCol[col] != PiiDetector.PiiKind.None)
                    {
                        row[col] = MaskString(raw);
                        maskedCols.Add(col);
                    }
                    else
                    {
                        row[col] = raw;
                    }
                }
                rows.Add(row);
            }

            sw.Stop();
            return new PreviewResult(
                Ok: true, Sql: pagedSql, Columns: columns, Rows: rows,
                MaskedColumns: maskedCols.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
                ElapsedMs: sw.ElapsedMilliseconds, Error: null);
        }
        catch (Exception ex)
        {
            return Fail(sw, ex.Message);
        }
    }

    private static PreviewResult Fail(System.Diagnostics.Stopwatch sw, string message)
    {
        sw.Stop();
        return new PreviewResult(
            Ok: false, Sql: null, Columns: Array.Empty<string>(),
            Rows: Array.Empty<Dictionary<string, object?>>(),
            MaskedColumns: Array.Empty<string>(),
            ElapsedMs: sw.ElapsedMilliseconds, Error: message);
    }

    /// <summary>
    /// 在 read SQL 外包一層 row limiter。SQL Server 直接 TOP；Oracle 用 outer-query
    /// + ROWNUM（最相容，不依賴 12c+ FETCH FIRST）。
    /// </summary>
    private static string WrapWithRowLimit(DbProviderType provider, string innerSql, int n) => provider switch
    {
        DbProviderType.SqlServer => InjectSqlServerTop(innerSql, n),
        DbProviderType.Oracle => $"SELECT * FROM ({innerSql}) WHERE ROWNUM <= {n}",
        _ => innerSql,
    };

    private static string InjectSqlServerTop(string sql, int n)
    {
        // 將開頭的 "SELECT " 改成 "SELECT TOP N "
        // BuildReadSqlForPreview 開頭一定是 "SELECT "（開頭、單一 keyword）
        const string sel = "SELECT ";
        if (!sql.StartsWith(sel, StringComparison.OrdinalIgnoreCase))
            return sql;  // 防呆：拿不到的話原樣回，至少不會炸
        return sel + "TOP " + n + " " + sql[sel.Length..];
    }

    private static object? MaskString(object? value)
    {
        if (value is null) return null;
        if (value is string s)
        {
            if (s.Length <= 4) return s;
            return string.Concat(s[0], new string('*', s.Length - 2), s[^1]);
        }
        return value;  // 數值 / 日期 / blob 不遮（型別本身少 PII；遮會破壞型別直觀性）
    }
}
