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

    /// <summary>
    /// 成功完成後在「目標 DB」呼叫的 stored procedure（schema-qualified，例如 dbo.OnEtlCompleted）。
    /// 空字串 = 不呼叫。SP 參數見 EtlEngine.InvokePostRunSpAsync 註解。
    /// </summary>
    public string? PostSuccessSp { get; set; }

    /// <summary>失敗時呼叫的 SP（同樣於目標 DB）。空 = 不呼叫。</summary>
    public string? PostFailureSp { get; set; }

    /// <summary>
    /// 是否在 RunHistory 的 sample payload 中遮罩敏感字串值（PII 保護）。
    /// 開啟時：字串值 > 4 字元 → 保留前 1 字 + 後 1 字 + 中間以 * 替代 (e.g. "Alice" → "A***e")。
    /// 數值 / 日期 / 短字串 / null 不遮罩。
    /// </summary>
    public bool MaskSamplePayload { get; set; } = false;

    /// <summary>
    /// Schema drift 偵測政策：
    ///   Ignore — 不檢查
    ///   Warn   — 檢查並 audit，但執行
    ///   Fail   — 影響到 mapping 的差異 → 直接 fail-fast 不執行 ETL
    /// </summary>
    public SchemaDriftPolicy SchemaDriftPolicy { get; set; } = SchemaDriftPolicy.Warn;

    /// <summary>來源 schema 的快照 (JSON ColumnInfo[])，建立 / 編輯 / 重新捕捉時更新。</summary>
    public string? SourceSchemaSnapshotJson { get; set; }

    /// <summary>目標 schema 的快照 (JSON ColumnInfo[])。</summary>
    public string? TargetSchemaSnapshotJson { get; set; }

    /// <summary>快照建立時間（UTC）。</summary>
    public DateTime? SchemaSnapshotAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ColumnMapping> Mappings { get; set; } = new();
}
