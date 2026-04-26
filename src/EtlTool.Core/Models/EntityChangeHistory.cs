namespace EtlTool.Core.Models;

public enum EntityChangeAction
{
    Created = 1,
    Updated = 2,
    Deleted = 3,
}

/// <summary>
/// 記錄關鍵實體（Connection / EtlTask）的逐次變更：誰、何時、做了什麼，
/// 並保留 before / after 的 JSON 快照供 admin 比對 diff。
///
/// 銀行需求：稽核軌跡（audit trail）必須能回答「上次有人修改是誰、改了什麼」。
/// AuditEvent 只記文字訊息，本表記**完整 state 快照**，補強 admin 排查能力。
///
/// 注意：BeforeJson / AfterJson 不應包含密碼或加密 blob（連線字串）；
/// 各 repository 寫入前要先 redact。
/// </summary>
public class EntityChangeHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>例：Connection / EtlTask</summary>
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string EntityName { get; set; } = "";

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string ChangedBy { get; set; } = "system";
    public EntityChangeAction Action { get; set; }

    /// <summary>變更前的 JSON 快照（Created 時為 null）。</summary>
    public string? BeforeJson { get; set; }
    /// <summary>變更後的 JSON 快照（Deleted 時為 null）。</summary>
    public string? AfterJson { get; set; }

    /// <summary>人類可讀的變更摘要（例：「Name: A→B; Enabled: false→true」），admin 列表顯示用。</summary>
    public string? Summary { get; set; }
}
