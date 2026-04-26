using System.Text.Json;
using System.Text.Json.Serialization;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>FilterNode 樹的 JSON 序列化/反序列化（含多型 group/condition）。</summary>
public static class FilterTreeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FilterNodeJsonConverter(), new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static FilterNode? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<FilterNode>(json, Options);
    }

    public static string Serialize(FilterNode? node)
        => node is null ? "" : JsonSerializer.Serialize(node, Options);
}

internal sealed class FilterNodeJsonConverter : JsonConverter<FilterNode>
{
    // 直接呼叫 Deserialize<FilterGroup>/Deserialize<FilterCondition> 時不會再走本 converter
    // (本 converter 只負責 FilterNode 抽象型別)，所以不需要做去除避免遞迴。
    public override FilterNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("kind", out var kindEl))
            throw new JsonException("Missing 'kind' on FilterNode.");

        var kind = kindEl.GetString();
        return kind switch
        {
            "group" => JsonSerializer.Deserialize<FilterGroup>(root.GetRawText(), options),
            "condition" => JsonSerializer.Deserialize<FilterCondition>(root.GetRawText(), options),
            _ => throw new JsonException($"Unknown FilterNode kind: {kind}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, FilterNode value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case FilterGroup g:
                JsonSerializer.Serialize(writer, g, options);
                break;
            case FilterCondition c:
                JsonSerializer.Serialize(writer, c, options);
                break;
            default:
                throw new JsonException($"Unsupported FilterNode: {value.GetType().Name}");
        }
    }
}
