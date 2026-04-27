using System.Data;
using ClosedXML.Excel;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 用 ClosedXML 讀 .xlsx（不支援舊的 .xls — 該格式銀行已罕見且 ClosedXML 不支援）。
///
/// 行為：
///   - SheetName 空字串 → 第 1 個 sheet
///   - HasHeader=true → 第 1 列當欄位名；否則 col0, col1...
///   - 從第一個有資料 row 開始讀，遇到完全空白 row 即停（ClosedXML LastRowUsed）
///   - 儲存格型別保留（ClosedXML 回 .Value object — DateTime / number / string / bool / null）
///   - 大檔（10 萬列以上）會吃較多記憶體，因為 ClosedXML 整本載入。如要 streaming
///     可改用 OpenXml SDK 自寫 SAX-style reader，但對銀行典型「日結報表 = 幾千列」夠用。
///
/// 注意：ClosedXML 把空 cell 回 null；下游 IDataReader 會給 DBNull.Value。
/// </summary>
public sealed class ExcelFileSourceReader : IFileSourceReader
{
    public Task<FileSourceOpenResult> OpenAsync(string filePath, FileSourceConfig config, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException($"來源檔案不存在：{filePath}", filePath);

        var workbook = new XLWorkbook(filePath);
        IXLWorksheet sheet;
        if (string.IsNullOrEmpty(config.ExcelSheetName))
        {
            sheet = workbook.Worksheets.First();
        }
        else
        {
            if (!workbook.Worksheets.TryGetWorksheet(config.ExcelSheetName, out sheet!))
            {
                workbook.Dispose();
                throw new InvalidOperationException(
                    $"Excel 檔內找不到名為「{config.ExcelSheetName}」的 sheet。" +
                    $"可用 sheet：{string.Join(", ", workbook.Worksheets.Select(w => w.Name))}");
            }
        }

        // 取資料邊界
        var firstRow = sheet.FirstRowUsed();
        var lastRow = sheet.LastRowUsed();
        if (firstRow is null || lastRow is null)
        {
            workbook.Dispose();
            throw new InvalidOperationException("Excel sheet 是空的，沒有資料可讀。");
        }
        int firstRowNum = firstRow.RowNumber();
        int lastRowNum = lastRow.RowNumber();
        int firstColNum = firstRow.FirstCellUsed()!.Address.ColumnNumber;
        int lastColNum = firstRow.LastCellUsed()!.Address.ColumnNumber;

        IReadOnlyList<string> columnNames;
        int dataStartRow;
        if (config.HasHeader)
        {
            var headers = new List<string>();
            for (int c = firstColNum; c <= lastColNum; c++)
            {
                var v = sheet.Cell(firstRowNum, c).GetString();
                headers.Add(string.IsNullOrEmpty(v) ? $"col{c - firstColNum}" : v);
            }
            columnNames = headers;
            dataStartRow = firstRowNum + 1;
        }
        else
        {
            int colCount = lastColNum - firstColNum + 1;
            columnNames = Enumerable.Range(0, colCount).Select(i => $"col{i}").ToList();
            dataStartRow = firstRowNum;
        }

        // 把 sheet 轉成 DataTable，再用 DataTableReader 暴露成 IDataReader
        // （ClosedXML 沒有原生 IDataReader；自己實作 row-by-row enumerator 工作量大且
        // ClosedXML 已經 in-memory，所以 DataTable 不增記憶體成本）
        var dt = new DataTable();
        foreach (var col in columnNames) dt.Columns.Add(col, typeof(object));
        for (int r = dataStartRow; r <= lastRowNum; r++)
        {
            var row = dt.NewRow();
            bool any = false;
            for (int c = firstColNum; c <= lastColNum; c++)
            {
                int colIdx = c - firstColNum;
                var cell = sheet.Cell(r, c);
                object? val = cell.IsEmpty() ? null : cell.Value.ToObject();
                row[colIdx] = val ?? DBNull.Value;
                if (val is not null) any = true;
            }
            if (any) dt.Rows.Add(row);  // 跳過全空 row（excel 常見幽靈空白）
        }

        // 載完 detach workbook（把 file lock 釋放）
        workbook.Dispose();

        IDataReader reader = dt.CreateDataReader();
        return Task.FromResult(new FileSourceOpenResult(reader, columnNames, info.Length));
    }
}

internal static class XLCellValueExtensions
{
    /// <summary>
    /// 把 ClosedXML 0.105+ 的 XLCellValue 轉成 plain .NET object。
    /// 不同 cell 型別 (Number / DateTime / TimeSpan / Text / Boolean / Blank / Error) 對應到
    /// IDataReader 自然能處理的型別。
    /// </summary>
    public static object? ToObject(this XLCellValue v) => v.Type switch
    {
        XLDataType.Blank => null,
        XLDataType.Boolean => v.GetBoolean(),
        XLDataType.Number => v.GetNumber(),
        XLDataType.Text => v.GetText(),
        XLDataType.Error => null,
        XLDataType.DateTime => v.GetDateTime(),
        XLDataType.TimeSpan => v.GetTimeSpan(),
        _ => v.ToString(),
    };
}
