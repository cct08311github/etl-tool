using System.Data;

namespace EtlTool.Core.Engine;

/// <summary>
/// 給 file-source 路徑用的最小 IDataRecord 實作 — 把已讀好的 (columns, values)
/// 包成 IDataRecord，方便 <see cref="TransformEvaluator"/> 用一份程式碼
/// 同時處理 DB reader 與 file reader 的 row。
///
/// 為什麼不直接傳 IDataReader 進 Evaluator？
///   - DbDataReader.Read() 是 stateful；TransformEvaluator 需要 by-ordinal & by-name 取值
///   - File reader 已 advance 到當前 row，但對外要看起來像「一筆 immutable record」
///   - 這個 adapter 是 in-memory 投影，零 IO，cost 可忽略
/// </summary>
internal sealed class ArrayRecord : IDataRecord
{
    private readonly IReadOnlyList<string> _names;
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _byName;

    public ArrayRecord(IReadOnlyList<string> names, object?[] values, IReadOnlyDictionary<string, int> byName)
    {
        _names = names;
        _values = values;
        _byName = byName;
    }

    public int FieldCount => _values.Length;

    public object this[int i] => _values[i] ?? DBNull.Value;
    public object this[string name] => _values[_byName[name]] ?? DBNull.Value;

    public string GetName(int i) => _names[i];
    public int GetOrdinal(string name) =>
        _byName.TryGetValue(name, out var i) ? i : throw new IndexOutOfRangeException(name);

    public object GetValue(int i) => _values[i] ?? DBNull.Value;
    public bool IsDBNull(int i) => _values[i] is null;

    public string GetDataTypeName(int i) => "Object";
    public Type GetFieldType(int i) => _values[i]?.GetType() ?? typeof(object);

    public bool GetBoolean(int i) => Convert.ToBoolean(_values[i]);
    public byte GetByte(int i) => Convert.ToByte(_values[i]);
    public char GetChar(int i) => Convert.ToChar(_values[i]);
    public DateTime GetDateTime(int i) => Convert.ToDateTime(_values[i]);
    public decimal GetDecimal(int i) => Convert.ToDecimal(_values[i]);
    public double GetDouble(int i) => Convert.ToDouble(_values[i]);
    public float GetFloat(int i) => Convert.ToSingle(_values[i]);
    public short GetInt16(int i) => Convert.ToInt16(_values[i]);
    public int GetInt32(int i) => Convert.ToInt32(_values[i]);
    public long GetInt64(int i) => Convert.ToInt64(_values[i]);
    public Guid GetGuid(int i) => _values[i] is Guid g ? g : Guid.Parse(_values[i]?.ToString() ?? "");
    public string GetString(int i) => _values[i]?.ToString() ?? "";

    public int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, _values.Length);
        for (int i = 0; i < n; i++) values[i] = _values[i] ?? DBNull.Value;
        return n;
    }

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
        => throw new NotSupportedException("ArrayRecord does not support GetBytes.");
    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
        => throw new NotSupportedException("ArrayRecord does not support GetChars.");
    public IDataReader GetData(int i) => throw new NotSupportedException();
}
