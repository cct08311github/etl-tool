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

    /// <summary>讀取最少筆數；null = 不檢查。</summary>
    public long? MinExpectedRows { get; set; }

    /// <summary>讀取最多筆數；null = 不檢查。</summary>
    public long? MaxExpectedRows { get; set; }

    /// <summary>row count 斷言違反時的處置：Ignore / Warn / Fail (rollback)。</summary>
    public EtlTool.Core.Engine.RowCountAssertionPolicy RowCountPolicy { get; set; }
        = EtlTool.Core.Engine.RowCountAssertionPolicy.Warn;

    /// <summary>
    /// 此任務專屬的 RunHistory 保留筆數覆寫；null = 套用全域 RunHistory:KeepLastPerTask 設定。
    /// 銀行常見：高風險任務（人事資料、財務）保留 365 天，其他任務套全域 100 筆即可。
    /// </summary>
    public int? RunHistoryRetentionRuns { get; set; }

    /// <summary>
    /// 預期最長執行時間（分鐘）。任務執行超過此值，watchdog 會發 Warning audit。
    /// null = 套用全域 LongRunningJob:MaxMinutes 預設；&lt;=0 視為停用。
    /// 不會強制 cancel job — 只是觀察與通知。
    /// </summary>
    public int? MaxRunMinutes { get; set; }

    /// <summary>
    /// 連續失敗 N 次後自動停用此任務的 circuit-breaker 閾值。
    /// null = 套用全域 Reliability:AutoDisableAfterFailures（預設 0 = 停用）。
    /// 銀行情境：避免一個壞掉的任務每分鐘失敗一次，連續打爆來源 DB / 灌爆 audit log。
    /// 觸發時：Enabled 設為 false、寫 Warning audit、重新排程；Admin 修復後手動重啟。
    /// </summary>
    public int? AutoDisableAfterFailures { get; set; }

    /// <summary>
    /// 由 circuit-breaker 觸發 auto-disable 的時間（UTC）。null = 不是 auto-disable
    /// 或已被 admin 手動重新啟用後清除。配合 AutoDisabledReason 給 UI 顯示前因後果。
    /// </summary>
    public DateTime? AutoDisabledAt { get; set; }

    /// <summary>auto-disable 的原因（人類可讀，含失敗次數與最後錯誤摘要）。</summary>
    public string? AutoDisabledReason { get; set; }

    /// <summary>
    /// Ops 交接 / 文件用的自由文字欄位（最大 2000 字）。
    /// 範例：「業主：alice@bank；on-call：bob@bank；02-04 維護期間需先停 task。」
    /// 顯示在 TaskEdit 表單與 Tasks list tooltip。不影響執行邏輯。
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ColumnMapping> Mappings { get; set; } = new();
}
