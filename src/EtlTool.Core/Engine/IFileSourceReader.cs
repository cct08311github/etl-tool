using System.Data;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把實體檔案轉成 IDataReader 串流，給 EtlEngine 沿用既有 BulkWriter / UpsertWriter。
///
/// 設計意圖：
///   - 介面回 <see cref="IDataReader"/> 而不是 List&lt;Dictionary&gt; — 為了 streaming 大檔
///     不爆記憶體（CSV / Excel 都可幾百 MB）
///   - <see cref="OpenAsync"/> 回 (reader, columnNames) — column 名稱在開檔當下決定，
///     之後 reader 移動 row 不會變
///   - reader 用完呼叫端負責 dispose（using）
///
/// 實作對應 <see cref="FileSourceFormat"/>：
///   - Csv → CsvFileSourceReader
///   - Excel → ExcelFileSourceReader
///   - DelimitedText → DelimitedTextFileSourceReader（與 Csv 共用大部分邏輯，自訂分隔字元）
/// </summary>
public interface IFileSourceReader
{
    Task<FileSourceOpenResult> OpenAsync(string filePath, FileSourceConfig config, CancellationToken ct);
}

/// <summary>
/// 開檔回傳結果。Reader 為 IDataReader 串流；ColumnNames 給 EtlEngine 用來
/// 對 ColumnMapping.SourceColumn 做 schema check。
/// </summary>
public sealed record FileSourceOpenResult(
    IDataReader Reader,
    IReadOnlyList<string> ColumnNames,
    long FileSizeBytes);

public static class FileSourceReaderFactory
{
    public static IFileSourceReader Create(FileSourceFormat format) => format switch
    {
        FileSourceFormat.Csv => new CsvFileSourceReader(),
        FileSourceFormat.Excel => new ExcelFileSourceReader(),
        FileSourceFormat.DelimitedText => new DelimitedTextFileSourceReader(),
        FileSourceFormat.FixedWidth => new FixedWidthFileSourceReader(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
