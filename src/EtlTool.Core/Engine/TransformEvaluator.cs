using System.Data;
using DynamicExpresso;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把 ColumnMapping.TransformExpression 預先編譯成 lambda；執行期 (per row) 直接呼叫。
/// 上下文：表達式內可用 row[\"COL_NAME\"] 取值，可使用 Convert、字串方法、DateTime 等。
/// </summary>
public sealed class TransformEvaluator
{
    private readonly List<CompiledMapping> _items;

    private TransformEvaluator(List<CompiledMapping> items)
    {
        _items = items;
    }

    public static TransformEvaluator Compile(IReadOnlyList<ColumnMapping> mappings)
    {
        var interp = new Interpreter()
            .Reference(typeof(Convert))
            .Reference(typeof(DateTime))
            .Reference(typeof(string))
            .Reference(typeof(decimal))
            .Reference(typeof(int))
            .Reference(typeof(long))
            .Reference(typeof(double));

        var items = new List<CompiledMapping>(mappings.Count);
        foreach (var m in mappings)
        {
            Func<IDataRecord, object?>? expr = null;
            if (!string.IsNullOrWhiteSpace(m.TransformExpression))
            {
                try
                {
                    var lambda = interp.ParseAsDelegate<Func<IDataRecord, object?>>(
                        m.TransformExpression!, "row");
                    expr = lambda;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Invalid transform expression for {m.SourceColumn} → {m.TargetColumn}: {ex.Message}",
                        ex);
                }
            }
            items.Add(new CompiledMapping(m.SourceColumn, m.TargetColumn, m.IsKey, expr));
        }
        return new TransformEvaluator(items);
    }

    public IReadOnlyList<CompiledMapping> Mappings => _items;

    /// <summary>
    /// 把一列來源資料 (record) 轉換成目標欄位順序的 object?[]。
    /// </summary>
    public object?[] Project(IDataRecord record)
    {
        var arr = new object?[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            var m = _items[i];
            if (m.Expression is not null)
            {
                arr[i] = m.Expression(record);
            }
            else
            {
                var ord = record.GetOrdinal(m.SourceColumn);
                arr[i] = record.IsDBNull(ord) ? null : record.GetValue(ord);
            }
        }
        return arr;
    }
}

public sealed record CompiledMapping(
    string SourceColumn,
    string TargetColumn,
    bool IsKey,
    Func<IDataRecord, object?>? Expression);
