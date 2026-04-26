namespace EtlTool.Core.Models;

public class EtlTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public Guid SourceConnectionId { get; set; }
    public string SourceSchema { get; set; } = "";
    public string SourceTable { get; set; } = "";

    public Guid TargetConnectionId { get; set; }
    public string TargetSchema { get; set; } = "";
    public string TargetTable { get; set; } = "";

    public WriteMode WriteMode { get; set; } = WriteMode.DeleteInsert;
    public FilterMode FilterMode { get; set; } = FilterMode.FormBuilder;

    /// <summary>表單模式時的條件樹 JSON（FilterGroup 序列化）</summary>
    public string? FilterFormJson { get; set; }

    /// <summary>進階模式時的純 WHERE 子句（不含 WHERE 關鍵字）</summary>
    public string? FilterRawSql { get; set; }

    public bool DeleteWhereSameAsFilter { get; set; } = true;
    public string? DeleteWhereRawSql { get; set; }

    public int BatchSize { get; set; } = 1000;
    public string CronExpression { get; set; } = "0 0 * * * ?";

    /// <summary>失敗時重試次數（不含第一次）。0 = 不重試。</summary>
    public int MaxRetries { get; set; } = 0;

    /// <summary>第一次重試前等待秒數。</summary>
    public int RetryDelaySeconds { get; set; } = 60;

    /// <summary>每次重試延遲倍數（exponential backoff）。1.0 = 固定間隔；2.0 = 60s, 120s, 240s...</summary>
    public double RetryBackoffMultiplier { get; set; } = 2.0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ColumnMapping> Mappings { get; set; } = new();
}
