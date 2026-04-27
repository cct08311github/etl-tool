using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 驗證 EtlTask 是否完整、可儲存、可執行。
/// 不依賴 connector，因此可在 UI 端「儲存前」呼叫做即時檢查。
/// </summary>
public static class EtlTaskValidator
{
    public static List<string> Validate(EtlTask task)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(task.Name))
            errors.Add("任務名稱不可為空");

        // Source 驗證 — 依 SourceKind 分流
        if (task.SourceKind == SourceKind.Database)
        {
            if (task.SourceConnectionId == Guid.Empty) errors.Add("請選擇來源連線");
            if (string.IsNullOrEmpty(task.SourceSchema)) errors.Add("請選擇來源 Schema");
            if (string.IsNullOrEmpty(task.SourceTable)) errors.Add("請選擇來源 Table");
        }
        else  // SourceKind.File
        {
            if (string.IsNullOrEmpty(task.FileSourceConfigJson))
                errors.Add("檔案模式下尚未設定來源檔案參數");
            else
            {
                FileSourceConfig? cfg = null;
                try { cfg = System.Text.Json.JsonSerializer.Deserialize<FileSourceConfig>(task.FileSourceConfigJson); }
                catch { errors.Add("FileSourceConfigJson 解析失敗（資料異常）"); }

                if (cfg is not null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.DirectoryPath)) errors.Add("檔案模式：請填來源目錄");
                    if (string.IsNullOrWhiteSpace(cfg.GlobPattern)) errors.Add("檔案模式：請填檔名 glob 樣式");
                    if (cfg.PostAction == FilePostAction.Archive
                        && !string.IsNullOrWhiteSpace(cfg.ArchiveDirectory)
                        && !string.IsNullOrWhiteSpace(cfg.DirectoryPath)
                        && string.Equals(
                            System.IO.Path.GetFullPath(cfg.ArchiveDirectory.Trim()),
                            System.IO.Path.GetFullPath(cfg.DirectoryPath.Trim()),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("檔案模式：歸檔目錄不可與來源目錄相同");
                    }
                }
            }
        }

        if (task.TargetConnectionId == Guid.Empty) errors.Add("請選擇目標連線");
        if (string.IsNullOrEmpty(task.TargetSchema)) errors.Add("請選擇目標 Schema");
        if (string.IsNullOrEmpty(task.TargetTable)) errors.Add("請選擇目標 Table");

        if (task.SourceKind == SourceKind.Database
            && task.SourceConnectionId != Guid.Empty
            && task.SourceConnectionId == task.TargetConnectionId
            && task.SourceSchema == task.TargetSchema
            && task.SourceTable == task.TargetTable
            && !string.IsNullOrEmpty(task.SourceTable))
        {
            errors.Add("來源與目標不可指向同一張表");
        }

        if (task.Mappings.Count == 0)
        {
            errors.Add("至少需設定一條欄位映射");
        }
        else
        {
            if (task.Mappings.Any(m =>
                string.IsNullOrEmpty(m.SourceColumn) || string.IsNullOrEmpty(m.TargetColumn)))
            {
                errors.Add("有映射的來源欄位或目標欄位為空，請補齊或刪除該列");
            }

            var dups = task.Mappings
                .Where(m => !string.IsNullOrEmpty(m.TargetColumn))
                .GroupBy(m => m.TargetColumn)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (dups.Count > 0)
                errors.Add($"目標欄位重複：{string.Join(", ", dups)}");
        }

        if (task.WriteMode == WriteMode.Upsert && !task.Mappings.Any(m => m.IsKey))
            errors.Add("Upsert 模式至少需一個映射勾選為主鍵 (IsKey)");

        if (task.BatchSize <= 0 || task.BatchSize > 100000)
            errors.Add("批次大小需在 1 ~ 100000 之間");

        if (task.MaxRetries < 0 || task.MaxRetries > 10)
            errors.Add("重試次數需在 0 ~ 10 之間");
        if (task.RetryDelaySeconds < 1 || task.RetryDelaySeconds > 3600)
            errors.Add("重試延遲秒數需在 1 ~ 3600 之間");
        if (task.RetryBackoffMultiplier < 1.0 || task.RetryBackoffMultiplier > 10.0)
            errors.Add("重試退避倍數需在 1.0 ~ 10.0 之間");

        // Stored Procedure 名稱基本檢查（schema.name 或 name；長度上限 200）
        foreach (var (label, sp) in new[]
        {
            ("成功後 SP", task.PostSuccessSp),
            ("失敗後 SP", task.PostFailureSp),
        })
        {
            if (string.IsNullOrWhiteSpace(sp)) continue;
            if (sp.Length > 200) errors.Add($"{label} 名稱過長（>200 字）");
            if (sp.Contains(';') || sp.Contains("--") || sp.Contains("/*"))
                errors.Add($"{label} 名稱含非法字元（; 或註解符號）");
        }

        try
        {
            _ = new Quartz.CronExpression(task.CronExpression);
        }
        catch (Exception ex)
        {
            errors.Add($"Cron 表達式無效：{ex.Message}");
        }

        if (task.FilterMode == FilterMode.FormBuilder
            && !string.IsNullOrWhiteSpace(task.FilterFormJson))
        {
            try { _ = FilterTreeJson.Deserialize(task.FilterFormJson!); }
            catch (Exception ex) { errors.Add($"篩選表單格式錯誤：{ex.Message}"); }
        }

        return errors;
    }
}
