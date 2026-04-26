namespace EtlTool.Core.Models;

public class ColumnMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EtlTaskId { get; set; }

    public string SourceColumn { get; set; } = "";
    public string TargetColumn { get; set; } = "";

    public bool IsKey { get; set; }

    /// <summary>DynamicExpresso 表達式（null 代表直接複製）。可使用 row["colName"]、Convert.* 等。</summary>
    public string? TransformExpression { get; set; }

    public int OrderIndex { get; set; }
}
