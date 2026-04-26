namespace EtlTool.Core.Models;

public enum ApprovalAction
{
    DeleteConnection = 1,
    DeleteTask = 2,
}

public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
}

/// <summary>
/// 兩人覆核請求。高風險動作（刪除連線/任務）必須由 A 提交、B 核准。
/// 防止單一帳號被盜後造成資料不可逆損失。
/// </summary>
public class ApprovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ApprovalAction Action { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    /// <summary>例：Connection / EtlTask</summary>
    public string TargetType { get; set; } = "";
    public Guid TargetId { get; set; }
    /// <summary>提交當下的 target 顯示名（即使後續被刪也保留語意）。</summary>
    public string TargetName { get; set; } = "";

    public string SubmittedBy { get; set; } = "";
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public string? SubmissionReason { get; set; }

    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }

    /// <summary>過期時間（建議 7 天）。BackgroundService 會把過期 Pending 改 Expired。</summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
}
