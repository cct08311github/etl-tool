using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把 CSV 文字解析成 EtlTask 候選列表（pure，不進 DB）。
/// 由呼叫端 (Razor page) 補上 ConnectionId 解析、執行 EtlTaskRepository.CreateAsync。
///
/// CSV 格式（header 必填，逗號分隔，引號包字串內含逗號）：
///   name,source_connection,source_schema,source_table,
///   target_connection,target_schema,target_table,
///   write_mode,cron,enabled,tags
///
/// - source_connection / target_connection 是 ConnectionDefinition.Name
/// - write_mode = DeleteInsert / Upsert
/// - enabled = true / false / yes / no / 1 / 0（缺省 = true）
/// - tags 可空；逗號 in tags 必須用引號包起來
/// </summary>
public static class TaskCsvImporter
{
    public sealed record ImportRow(
        int LineNumber,
        bool Ok,
        string? Error,
        string Name,
        string SourceConnection,
        string SourceSchema,
        string SourceTable,
        string TargetConnection,
        string TargetSchema,
        string TargetTable,
        WriteMode WriteMode,
        string CronExpression,
        bool Enabled,
        string? Tags);

    public sealed record ImportResult(
        IReadOnlyList<ImportRow> Rows,
        int OkCount,
        int ErrorCount);

    private static readonly string[] ExpectedHeaders =
    {
        "name","source_connection","source_schema","source_table",
        "target_connection","target_schema","target_table",
        "write_mode","cron","enabled","tags",
    };

    public static ImportResult Parse(string csvText)
    {
        var lines = SplitLines(csvText);
        if (lines.Count == 0)
            return new ImportResult(Array.Empty<ImportRow>(), 0, 0);

        // Header
        var headerCells = ParseCsvLine(lines[0]).Select(s => s.Trim().ToLowerInvariant()).ToList();
        var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerCells.Count; i++) headerIndex[headerCells[i]] = i;

        // Allow extra columns; require all expected ones
        var missing = ExpectedHeaders.Where(h => !headerIndex.ContainsKey(h)).ToList();
        if (missing.Count > 0)
        {
            var msg = "Missing required columns: " + string.Join(", ", missing);
            return new ImportResult(
                new[] { new ImportRow(1, false, msg, "", "", "", "", "", "", "", default, "", false, null) },
                0, 1);
        }

        var rows = new List<ImportRow>();
        for (int li = 1; li < lines.Count; li++)
        {
            var raw = lines[li];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var cells = ParseCsvLine(raw);
            if (cells.Count == 0) continue;

            string Get(string key) =>
                headerIndex.TryGetValue(key, out var idx) && idx < cells.Count
                    ? cells[idx].Trim()
                    : "";

            var name = Get("name");
            var sourceConn = Get("source_connection");
            var sourceSchema = Get("source_schema");
            var sourceTable = Get("source_table");
            var targetConn = Get("target_connection");
            var targetSchema = Get("target_schema");
            var targetTable = Get("target_table");
            var writeModeStr = Get("write_mode");
            var cron = Get("cron");
            var enabledStr = Get("enabled");
            var tags = Get("tags");

            // Validate
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) errors.Add("name is empty");
            if (string.IsNullOrWhiteSpace(sourceConn)) errors.Add("source_connection is empty");
            if (string.IsNullOrWhiteSpace(targetConn)) errors.Add("target_connection is empty");
            if (string.IsNullOrWhiteSpace(sourceTable)) errors.Add("source_table is empty");
            if (string.IsNullOrWhiteSpace(targetTable)) errors.Add("target_table is empty");
            if (string.IsNullOrWhiteSpace(cron)) errors.Add("cron is empty");

            WriteMode writeMode;
            if (!Enum.TryParse(writeModeStr, ignoreCase: true, out writeMode))
            {
                errors.Add($"write_mode '{writeModeStr}' invalid (expected DeleteInsert / Upsert)");
                writeMode = default;
            }

            bool enabled = ParseBool(enabledStr, defaultValue: true);

            try { _ = new Quartz.CronExpression(cron); }
            catch (Exception ex) { errors.Add($"cron invalid: {ex.Message}"); }

            var ok = errors.Count == 0;
            rows.Add(new ImportRow(
                LineNumber: li + 1,    // 1-based, accounting for header
                Ok: ok,
                Error: ok ? null : string.Join("; ", errors),
                Name: name,
                SourceConnection: sourceConn,
                SourceSchema: sourceSchema,
                SourceTable: sourceTable,
                TargetConnection: targetConn,
                TargetSchema: targetSchema,
                TargetTable: targetTable,
                WriteMode: writeMode,
                CronExpression: cron,
                Enabled: enabled,
                Tags: string.IsNullOrWhiteSpace(tags) ? null : tags));
        }

        return new ImportResult(rows, rows.Count(r => r.Ok), rows.Count(r => !r.Ok));
    }

    /// <summary>把驗證後的 ImportRow 轉成 EtlTask（呼叫端解析 connection name → id）。</summary>
    public static EtlTask ToEtlTask(ImportRow row, Guid sourceConnId, Guid targetConnId) => new()
    {
        Name = row.Name,
        SourceConnectionId = sourceConnId,
        SourceSchema = row.SourceSchema,
        SourceTable = row.SourceTable,
        TargetConnectionId = targetConnId,
        TargetSchema = row.TargetSchema,
        TargetTable = row.TargetTable,
        WriteMode = row.WriteMode,
        CronExpression = row.CronExpression,
        Enabled = row.Enabled,
        Tags = row.Tags,
    };

    private static bool ParseBool(string s, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        return s.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" or "on" => true,
            "false" or "no" or "n" or "0" or "off" => false,
            _ => defaultValue,
        };
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        // Naive — does not handle CR LF inside quoted strings; that's a rare CSV edge case.
        // For our admin-import use case (typed by hand or excel-export), good enough.
        foreach (var line in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            lines.Add(line);
        // Trim trailing blank line common in copy-paste
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>RFC 4180 parser — handles "" escape and commas inside quotes.</summary>
    public static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',')
                {
                    cells.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"' && sb.Length == 0)
                {
                    inQuotes = true;
                }
                else sb.Append(c);
            }
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
