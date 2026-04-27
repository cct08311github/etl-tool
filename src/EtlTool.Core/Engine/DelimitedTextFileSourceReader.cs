using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 一般化的分隔字元文字檔 reader。底層直接重用 <see cref="CsvFileSourceReader"/>
/// — 兩者唯一差別是預設欄位分隔字元（CSV 預設逗號；DelimitedText 通常用 tab / pipe）。
///
/// 銀行常見：
///   - 主機系統匯出 .txt with TAB（pipe-delimited 也常見）
///   - 自訂 record format（| 或 ; 為分隔）— 設 Delimiter 即可
///
/// 為什麼分開類別？
///   - UI 上「CSV」vs「文字檔」是不同心智模型；統一成 IFileSourceReader 讓 factory 對應
///   - 未來 fixed-width / 不同 quote 規則可獨立演化，不污染 CsvFileSourceReader
/// </summary>
public sealed class DelimitedTextFileSourceReader : IFileSourceReader
{
    private readonly CsvFileSourceReader _csv = new();

    public Task<FileSourceOpenResult> OpenAsync(string filePath, FileSourceConfig config, CancellationToken ct)
    {
        // 預設用 tab — 「文字檔」最常見的分隔
        if (string.IsNullOrEmpty(config.Delimiter) || config.Delimiter == ",")
        {
            config = new FileSourceConfig
            {
                DirectoryPath = config.DirectoryPath,
                GlobPattern = config.GlobPattern,
                Format = config.Format,
                Encoding = config.Encoding,
                HasHeader = config.HasHeader,
                Delimiter = "\\t",
                ExcelSheetName = config.ExcelSheetName,
                PostAction = config.PostAction,
                ArchiveDirectory = config.ArchiveDirectory,
                MaxFilesPerRun = config.MaxFilesPerRun,
            };
        }
        return _csv.OpenAsync(filePath, config, ct);
    }
}
