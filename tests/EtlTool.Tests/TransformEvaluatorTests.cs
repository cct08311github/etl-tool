using System.Data;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class TransformEvaluatorTests
{
    [Fact]
    public void Direct_copy_when_no_expression()
    {
        var mappings = new[]
        {
            new ColumnMapping { SourceColumn = "NAME", TargetColumn = "name" },
            new ColumnMapping { SourceColumn = "AGE", TargetColumn = "age" },
        };
        var ev = TransformEvaluator.Compile(mappings);

        var record = new FakeRecord(new() { ["NAME"] = "Alice", ["AGE"] = 30 });
        var row = ev.Project(record);
        Assert.Equal("Alice", row[0]);
        Assert.Equal(30, row[1]);
    }

    [Fact]
    public void Expression_runs_and_transforms_value()
    {
        var mappings = new[]
        {
            new ColumnMapping
            {
                SourceColumn = "NAME", TargetColumn = "upper_name",
                TransformExpression = "row[\"NAME\"].ToString().ToUpper()",
            },
        };
        var ev = TransformEvaluator.Compile(mappings);
        var record = new FakeRecord(new() { ["NAME"] = "Alice" });
        var row = ev.Project(record);
        Assert.Equal("ALICE", row[0]);
    }

    [Fact]
    public void Null_source_value_becomes_null_when_direct_copy()
    {
        var mappings = new[]
        {
            new ColumnMapping { SourceColumn = "X", TargetColumn = "x" },
        };
        var ev = TransformEvaluator.Compile(mappings);
        var record = new FakeRecord(new() { ["X"] = DBNull.Value });
        var row = ev.Project(record);
        Assert.Null(row[0]);
    }

    [Fact]
    public void Bad_expression_throws_during_compile()
    {
        var mappings = new[]
        {
            new ColumnMapping
            {
                SourceColumn = "X", TargetColumn = "x",
                TransformExpression = "this is not C#",
            },
        };
        Assert.Throws<InvalidOperationException>(() => TransformEvaluator.Compile(mappings));
    }

    private sealed class FakeRecord : IDataRecord
    {
        private readonly List<KeyValuePair<string, object?>> _data;
        public FakeRecord(Dictionary<string, object?> data)
        {
            _data = data.ToList();
        }
        public int FieldCount => _data.Count;
        public int GetOrdinal(string name) => _data.FindIndex(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        public string GetName(int i) => _data[i].Key;
        public object? GetValue(int i) => _data[i].Value;
        public bool IsDBNull(int i) => _data[i].Value is DBNull or null;

        // boilerplate
        public object this[int i] => GetValue(i)!;
        public object this[string name] => GetValue(GetOrdinal(name))!;
        public bool GetBoolean(int i) => Convert.ToBoolean(GetValue(i));
        public byte GetByte(int i) => Convert.ToByte(GetValue(i));
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => Convert.ToChar(GetValue(i)!);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => GetValue(i)?.GetType().Name ?? "null";
        public DateTime GetDateTime(int i) => Convert.ToDateTime(GetValue(i));
        public decimal GetDecimal(int i) => Convert.ToDecimal(GetValue(i));
        public double GetDouble(int i) => Convert.ToDouble(GetValue(i));
        public Type GetFieldType(int i) => GetValue(i)?.GetType() ?? typeof(object);
        public float GetFloat(int i) => Convert.ToSingle(GetValue(i));
        public Guid GetGuid(int i) => (Guid)GetValue(i)!;
        public short GetInt16(int i) => Convert.ToInt16(GetValue(i));
        public int GetInt32(int i) => Convert.ToInt32(GetValue(i));
        public long GetInt64(int i) => Convert.ToInt64(GetValue(i));
        public string GetString(int i) => Convert.ToString(GetValue(i)) ?? "";
        public int GetValues(object[] values) { for (int i = 0; i < _data.Count; i++) values[i] = _data[i].Value!; return _data.Count; }
    }
}
