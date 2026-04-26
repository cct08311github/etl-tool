namespace EtlTool.Core.Models;

public sealed record ColumnInfo(
    string Name,
    string DataType,
    bool Nullable,
    bool IsPrimaryKey,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null);
