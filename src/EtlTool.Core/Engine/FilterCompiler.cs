using System.Globalization;
using System.Text;
using EtlTool.Core.Connectors;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

public sealed record CompiledFilter(string WhereSql, IReadOnlyList<(string Name, object? Value)> Parameters)
{
    public static CompiledFilter Empty { get; } = new(string.Empty, Array.Empty<(string, object?)>());
}

/// <summary>
/// 把 FilterNode 樹編譯成 (parameterized WHERE, params)。
/// 表達式結果不含 "WHERE" 關鍵字，由呼叫端拼接。
/// </summary>
public sealed class FilterCompiler
{
    private readonly IDbConnector _connector;

    public FilterCompiler(IDbConnector connector)
    {
        _connector = connector;
    }

    public CompiledFilter Compile(FilterNode? root)
    {
        if (root is null) return CompiledFilter.Empty;

        var sb = new StringBuilder();
        var parameters = new List<(string, object?)>();
        var ctx = new CompileContext(parameters);
        Visit(root, sb, ctx);
        return new CompiledFilter(sb.ToString(), parameters);
    }

    private void Visit(FilterNode node, StringBuilder sb, CompileContext ctx)
    {
        switch (node)
        {
            case FilterGroup g:
                if (g.Children.Count == 0) { sb.Append("1=1"); return; }
                sb.Append('(');
                for (int i = 0; i < g.Children.Count; i++)
                {
                    if (i > 0) sb.Append(g.Logic == FilterLogic.And ? " AND " : " OR ");
                    Visit(g.Children[i], sb, ctx);
                }
                sb.Append(')');
                break;

            case FilterCondition c:
                CompileCondition(c, sb, ctx);
                break;

            default:
                throw new InvalidOperationException($"Unknown filter node: {node.GetType().Name}");
        }
    }

    private void CompileCondition(FilterCondition c, StringBuilder sb, CompileContext ctx)
    {
        if (string.IsNullOrWhiteSpace(c.Column))
            throw new ArgumentException("Filter condition column cannot be empty.");

        var col = _connector.QuoteIdentifier(c.Column);

        switch (c.Operator)
        {
            case FilterOperator.IsNull:
                sb.Append(col).Append(" IS NULL");
                break;

            case FilterOperator.IsNotNull:
                sb.Append(col).Append(" IS NOT NULL");
                break;

            case FilterOperator.Eq:
            case FilterOperator.NotEq:
            case FilterOperator.Gt:
            case FilterOperator.Gte:
            case FilterOperator.Lt:
            case FilterOperator.Lte:
            case FilterOperator.Like:
            case FilterOperator.NotLike:
                {
                    var op = c.Operator switch
                    {
                        FilterOperator.Eq => "=",
                        FilterOperator.NotEq => "<>",
                        FilterOperator.Gt => ">",
                        FilterOperator.Gte => ">=",
                        FilterOperator.Lt => "<",
                        FilterOperator.Lte => "<=",
                        FilterOperator.Like => "LIKE",
                        FilterOperator.NotLike => "NOT LIKE",
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                    var pname = ctx.NextParam();
                    sb.Append(col).Append(' ').Append(op).Append(' ').Append(_connector.ParameterPrefix).Append(pname);
                    ctx.AddParameter(pname, ParseValue(c.Value));
                    break;
                }

            case FilterOperator.In:
            case FilterOperator.NotIn:
                {
                    if (c.Values is null || c.Values.Count == 0)
                        throw new ArgumentException("IN/NOT IN requires at least one value.");
                    var op = c.Operator == FilterOperator.In ? "IN" : "NOT IN";
                    sb.Append(col).Append(' ').Append(op).Append(" (");
                    for (int i = 0; i < c.Values.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var pname = ctx.NextParam();
                        sb.Append(_connector.ParameterPrefix).Append(pname);
                        ctx.AddParameter(pname, ParseValue(c.Values[i]));
                    }
                    sb.Append(')');
                    break;
                }

            case FilterOperator.Between:
                {
                    if (c.Values is null || c.Values.Count != 2)
                        throw new ArgumentException("BETWEEN requires exactly two values.");
                    var p1 = ctx.NextParam();
                    var p2 = ctx.NextParam();
                    sb.Append(col).Append(" BETWEEN ")
                      .Append(_connector.ParameterPrefix).Append(p1)
                      .Append(" AND ")
                      .Append(_connector.ParameterPrefix).Append(p2);
                    ctx.AddParameter(p1, ParseValue(c.Values[0]));
                    ctx.AddParameter(p2, ParseValue(c.Values[1]));
                    break;
                }

            default:
                throw new InvalidOperationException($"Unsupported operator: {c.Operator}");
        }
    }

    /// <summary>
    /// 嘗試把字串值轉成最合適的 .NET 型別（整數→long、浮點→decimal、ISO 日期→DateTime，否則保留字串）。
    /// 由 ADO.NET driver 在最後一刻處理目標型別。
    /// </summary>
    private static object? ParseValue(string? raw)
    {
        if (raw is null) return null;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        return raw;
    }

    private sealed class CompileContext
    {
        private readonly List<(string, object?)> _params;
        private int _seq;
        public CompileContext(List<(string, object?)> ps) { _params = ps; }
        public string NextParam() => $"f{_seq++}";
        public void AddParameter(string name, object? value) => _params.Add((name, value));
    }
}
