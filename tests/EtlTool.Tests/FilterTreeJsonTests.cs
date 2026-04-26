using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class FilterTreeJsonTests
{
    [Fact]
    public void Roundtrip_group_with_conditions()
    {
        var original = new FilterGroup
        {
            Logic = FilterLogic.Or,
            Children =
            {
                new FilterCondition { Column = "AGE", Operator = FilterOperator.Gt, Value = "18" },
                new FilterGroup
                {
                    Logic = FilterLogic.And,
                    Children =
                    {
                        new FilterCondition { Column = "NAME", Operator = FilterOperator.Like, Value = "A%" },
                        new FilterCondition { Column = "DEPT", Operator = FilterOperator.In, Values = new() { "10", "20" } },
                    },
                },
            },
        };

        var json = FilterTreeJson.Serialize(original);
        var roundtripped = FilterTreeJson.Deserialize(json);

        Assert.IsType<FilterGroup>(roundtripped);
        var g = (FilterGroup)roundtripped!;
        Assert.Equal(FilterLogic.Or, g.Logic);
        Assert.Equal(2, g.Children.Count);

        var leaf = (FilterCondition)g.Children[0];
        Assert.Equal("AGE", leaf.Column);
        Assert.Equal(FilterOperator.Gt, leaf.Operator);
        Assert.Equal("18", leaf.Value);

        var nested = (FilterGroup)g.Children[1];
        Assert.Equal(FilterLogic.And, nested.Logic);
        Assert.Equal(2, nested.Children.Count);
        var inLeaf = (FilterCondition)nested.Children[1];
        Assert.NotNull(inLeaf.Values);
        Assert.Equal(new[] { "10", "20" }, inLeaf.Values);
    }

    [Fact]
    public void Empty_string_returns_null()
    {
        Assert.Null(FilterTreeJson.Deserialize(""));
    }
}
