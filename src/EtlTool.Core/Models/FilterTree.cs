using System.Text.Json.Serialization;

namespace EtlTool.Core.Models;

public enum FilterOperator
{
    Eq,
    NotEq,
    Gt,
    Gte,
    Lt,
    Lte,
    Like,
    NotLike,
    In,
    NotIn,
    Between,
    IsNull,
    IsNotNull,
}

public enum FilterLogic
{
    And,
    Or,
}

public abstract class FilterNode
{
    [JsonPropertyName("kind")]
    public abstract string Kind { get; }
}

public sealed class FilterGroup : FilterNode
{
    public override string Kind => "group";

    public FilterLogic Logic { get; set; } = FilterLogic.And;
    public List<FilterNode> Children { get; set; } = new();
}

public sealed class FilterCondition : FilterNode
{
    public override string Kind => "condition";

    public string Column { get; set; } = "";
    public FilterOperator Operator { get; set; } = FilterOperator.Eq;

    /// <summary>單值運算子的字串值（內部會依目標欄位型別轉型）</summary>
    public string? Value { get; set; }

    /// <summary>IN / NOT IN / BETWEEN 用</summary>
    public List<string>? Values { get; set; }
}
