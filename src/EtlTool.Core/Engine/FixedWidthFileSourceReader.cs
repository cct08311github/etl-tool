using System.Data;
using System.Text.Json;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 固定欄寬 (fixed-width) 文字檔讀取器。
///
/// 銀行 / 金融常見場景：
///   - 主機（z/OS / AS/400）每日批次匯出 .txt 報表，每行對應一筆紀錄，
///     欄位以「位置 + 長度」切割，沒有分隔字元
///   - COBOL FILE SECTION 會明確定義每個欄位的 PIC 寬度
///   - 通常以空白填滿到固定寬度（PIC X(10) → 不足 10 字元在右補空白）
///
/// 設計：
///   - 1-based start position（mainframe 規格慣例）
///   - Encoding 由 FileSourceConfig.Encoding（Big5 / EBCDIC 也支援，後者要使用者
///     手動裝對應 codepage 提供者）
///   - HasHeader 不適用 — 欄位名直接寫在 layout 裡
///   - 行短於某欄位起迄位置 → 該欄位回 null（DBNull）；不 throw，因為主機檔
///     常有「特殊 header / footer 行」比資料行短
///   - 純空白行跳過
/// </summary>
public sealed class FixedWidthFileSourceReader : IFileSourceReader
{
    public Task<FileSourceOpenResult> OpenAsync(string filePath, FileSourceConfig config, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException($"來源檔案不存在：{filePath}", filePath);

        if (string.IsNullOrWhiteSpace(config.FixedWidthLayoutJson))
            throw new InvalidOperationException(
                "Fixed-width 模式需要在 FileSourceConfig.FixedWidthLayoutJson 設定 layout（欄位 / 位置 / 長度）。");

        List<FixedWidthColumn> layout;
        try
        {
            layout = JsonSerializer.Deserialize<List<FixedWidthColumn>>(config.FixedWidthLayoutJson)
                ?? throw new InvalidOperationException("Layout JSON 解析為空。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FixedWidthLayoutJson 格式錯誤：{ex.Message}", ex);
        }

        if (layout.Count == 0)
            throw new InvalidOperationException("Fixed-width layout 至少需一個欄位。");
        foreach (var col in layout)
        {
            if (string.IsNullOrWhiteSpace(col.Name))
                throw new InvalidOperationException("Fixed-width layout 內有欄位 Name 為空。");
            if (col.Start < 1)
                throw new InvalidOperationException($"欄位「{col.Name}」Start 必須 ≥ 1（1-based）。");
            if (col.Length < 1)
                throw new InvalidOperationException($"欄位「{col.Name}」Length 必須 ≥ 1。");
        }

        var columnNames = layout.Select(c => c.Name).ToList();
        var encoding = CsvFileSourceReader.ResolveEncoding(config.Encoding);

        // 串流讀取：每次 Read() 才讀下一行。檔案 dispose 時關閉。
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        var textReader = new StreamReader(stream, encoding);
        IDataReader reader = new FixedWidthDataReader(textReader, layout);
        return Task.FromResult(new FileSourceOpenResult(reader, columnNames, info.Length));
    }
}

/// <summary>
/// 串流式 IDataReader：每次 <see cref="Read"/> 從底層 TextReader 拿一行，依 layout 切片。
/// Dispose 同時關掉底層 stream。
/// </summary>
internal sealed class FixedWidthDataReader : IDataReader
{
    private readonly TextReader _reader;
    private readonly IReadOnlyList<FixedWidthColumn> _layout;
    private readonly Dictionary<string, int> _ordinalByName;
    private object?[]? _current;
    private bool _isClosed;

    public FixedWidthDataReader(TextReader reader, IReadOnlyList<FixedWidthColumn> layout)
    {
        _reader = reader;
        _layout = layout;
        _ordinalByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < layout.Count; i++) _ordinalByName[layout[i].Name] = i;
    }

    public bool Read()
    {
        while (true)
        {
            if (_isClosed) return false;
            var line = _reader.ReadLine();
            if (line is null) return false;
            // 跳過完全空白的行（主機檔常見 trailer / 空 padding）
            if (string.IsNullOrWhiteSpace(line)) continue;

            _current = new object?[_layout.Count];
            for (int i = 0; i < _layout.Count; i++)
            {
                var col = _layout[i];
                int startIdx = col.Start - 1;
                if (startIdx >= line.Length)
                {
                    _current[i] = null;
                    continue;
                }
                int len = Math.Min(col.Length, line.Length - startIdx);
                var slice = line.Substring(startIdx, len);
                _current[i] = col.TrimWhitespace ? slice.Trim() : slice;
            }
            return true;
        }
    }

    public int FieldCount => _layout.Count;
    public bool IsClosed => _isClosed;
    public int Depth => 0;
    public int RecordsAffected => -1;

    public object this[int i] => _current?[i] ?? DBNull.Value;
    public object this[string name] => _current?[_ordinalByName[name]] ?? DBNull.Value;

    public string GetName(int i) => _layout[i].Name;
    public int GetOrdinal(string name) => _ordinalByName.TryGetValue(name, out var i)
        ? i : throw new IndexOutOfRangeException(name);

    public bool IsDBNull(int i) => _current is null || _current[i] is null;
    public object GetValue(int i) => _current?[i] ?? DBNull.Value;
    public string GetDataTypeName(int i) => "String";
    public Type GetFieldType(int i) => typeof(string);

    public string GetString(int i) => _current?[i]?.ToString() ?? "";
    public bool GetBoolean(int i) => Convert.ToBoolean(GetValue(i));
    public byte GetByte(int i) => Convert.ToByte(GetValue(i));
    public char GetChar(int i) => Convert.ToChar(GetValue(i));
    public DateTime GetDateTime(int i) => Convert.ToDateTime(GetValue(i));
    public decimal GetDecimal(int i) => Convert.ToDecimal(GetValue(i));
    public double GetDouble(int i) => Convert.ToDouble(GetValue(i));
    public float GetFloat(int i) => Convert.ToSingle(GetValue(i));
    public short GetInt16(int i) => Convert.ToInt16(GetValue(i));
    public int GetInt32(int i) => Convert.ToInt32(GetValue(i));
    public long GetInt64(int i) => Convert.ToInt64(GetValue(i));
    public Guid GetGuid(int i)
    {
        var v = GetValue(i);
        return v is Guid g ? g : Guid.Parse(v?.ToString() ?? "");
    }

    public int GetValues(object[] values)
    {
        if (_current is null) return 0;
        var n = Math.Min(values.Length, _current.Length);
        for (int i = 0; i < n; i++) values[i] = _current[i] ?? DBNull.Value;
        return n;
    }

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
        => throw new NotSupportedException();
    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
        => throw new NotSupportedException();
    public IDataReader GetData(int i) => throw new NotSupportedException();

    public DataTable? GetSchemaTable() => null;
    public bool NextResult() => false;

    public void Close() => Dispose();
    public void Dispose()
    {
        if (_isClosed) return;
        _isClosed = true;
        _reader.Dispose();
    }
}
