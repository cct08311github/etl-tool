using System.Data;
using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 用 CsvHelper 讀 RFC 4180 風格 CSV，包成 IDataReader（streaming）。
///
/// 設計：
///   - HasHeader=true → 用第一行當欄位名；ColumnMapping.SourceColumn 對應這些名稱
///   - HasHeader=false → 自動產 col0, col1, col2...
///   - Delimiter 由 config 給；常見 , 或 ; 或 |
///   - Encoding 由 config（UTF-8 / Big5 / GB18030 / ISO-8859-1 都支援）
///   - Quote / escape 走 CsvHelper 預設（雙引號 + " " 跳脫）
///
/// 銀行常見坑點：
///   - Big5 編碼的舊系統匯出檔 → encoding 設 big5
///   - Excel 另存 CSV 會帶 BOM → CsvHelper 自動處理
///   - 欄位內含換行 → 必須加雙引號包覆，CsvHelper 會正確 parse
/// </summary>
public sealed class CsvFileSourceReader : IFileSourceReader
{
    public Task<FileSourceOpenResult> OpenAsync(string filePath, FileSourceConfig config, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException($"來源檔案不存在：{filePath}", filePath);

        var encoding = ResolveEncoding(config.Encoding);
        var delimiter = ResolveDelimiter(config.Delimiter);

        // 注意：CsvDataReader 拿 stream / textreader 是 streaming；不會把全檔載進記憶體。
        // CsvDataReader dispose 時會 dispose 底層 reader，但底層 stream 要呼叫端管。
        // 這裡用 leaveOpen=false 把生命週期綁在一起。
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        var textReader = new StreamReader(stream, encoding);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = config.HasHeader,
            Delimiter = delimiter,
            BadDataFound = null,         // 不要 throw，銀行檔常有奇怪資料
            MissingFieldFound = null,    // 缺欄位回 null 而不是 throw
            DetectColumnCountChanges = false,
            TrimOptions = TrimOptions.None,  // 不自動 trim — 客戶有時要保留前後空白
            // 引號字元：預設 " 支援 "XXX","YYY" 與 "包含,逗號"；
            // 設空字串 → '\0' 等於關掉引號處理（純 delimiter 切）
            Quote = string.IsNullOrEmpty(config.QuoteCharacter) ? '\0' : config.QuoteCharacter[0],
            // 雙引號跳脫（"" 代表一個 "）— RFC 4180 標準
            Escape = string.IsNullOrEmpty(config.QuoteCharacter) ? '\0' : config.QuoteCharacter[0],
        };

        var csv = new CsvReader(textReader, csvConfig);

        // 取欄名 — 必須 advance reader
        IReadOnlyList<string> columnNames;
        if (config.HasHeader)
        {
            csv.Read();
            csv.ReadHeader();
            columnNames = csv.HeaderRecord ?? Array.Empty<string>();
        }
        else
        {
            // 先 read 一筆探出欄位數，再 reset — 但 CsvDataReader 不支援 reset，
            // 簡單作法：第一筆讀完後決定欄位數；CsvDataReader 會把那筆當第一筆 row 處理
            // 為了乾淨，先 peek 第一筆做欄數判定，然後 dispose + 重開
            csv.Read();
            int n = csv.Parser.Count;
            columnNames = Enumerable.Range(0, n).Select(i => $"col{i}").ToList();
            csv.Dispose();
            textReader.Dispose();
            stream.Dispose();
            // 重開
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            textReader = new StreamReader(stream, encoding);
            csv = new CsvReader(textReader, csvConfig);
        }

        IDataReader dataReader = new CsvDataReader(csv);
        return Task.FromResult(new FileSourceOpenResult(dataReader, columnNames, info.Length));
    }

    /// <summary>支援常見編碼名稱（不分大小寫）。Big5 對應 Windows-950 (cp950)。</summary>
    public static Encoding ResolveEncoding(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Encoding.UTF8;
        // CodePagesEncodingProvider 在 .NET Core 後預設沒註冊，需要顯式啟用以支援 big5 / gb18030
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var lower = raw.Trim().ToLowerInvariant();
        return lower switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "big5" or "cp950" or "windows-950" => Encoding.GetEncoding("big5"),
            "gb18030" or "gb2312" or "cp936" => Encoding.GetEncoding("gb18030"),
            "shift-jis" or "shift_jis" or "cp932" => Encoding.GetEncoding("shift_jis"),
            "iso-8859-1" or "latin1" => Encoding.GetEncoding("iso-8859-1"),
            _ => Encoding.GetEncoding(raw),  // 最後 fallback：直接吃使用者輸入；不認得 → 拋例外
        };
    }

    /// <summary>把使用者輸入的 \t 等字面 escape 還原。</summary>
    public static string ResolveDelimiter(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return ",";
        // 字面 \t / \r / \n
        return raw
            .Replace("\\t", "\t")
            .Replace("\\r", "\r")
            .Replace("\\n", "\n");
    }
}
