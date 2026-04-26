using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把事件落地成 AuditEvent。所有實作必須非阻塞（best-effort），且**不可吞** caller 的例外
/// — 寫 audit 失敗時把錯誤丟給 ILogger，但讓主流程繼續。
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(
        AuditCategory category,
        AuditAction action,
        string message,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        AuditSeverity severity = AuditSeverity.Info,
        string? detailsJson = null,
        string? actor = null,
        CancellationToken ct = default);
}
