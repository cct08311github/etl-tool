using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Core.Scheduling;

/// <summary>
/// Singleton 全域暫停旗標。為了應急（如：發現連線字串外洩、目標 DB 異常時）
/// 提供「一鍵停止所有 ETL」的能力。
///
/// 設計：
///   - 純 in-memory（重啟自動清空 = 預設啟用）
///     若銀行需要「重啟後仍保持暫停」可改 DB-backed（後續加）
///   - 不影響「手動觸發」(TriggerType.Manual)：admin 仍可在暫停下手動跑
///   - 影響：Scheduled 與 Retry 都會 skip
///   - 變更狀態時發 Audit 高 severity 事件
/// </summary>
public sealed class SchedulerKillSwitch
{
    private readonly IAuditLogger? _audit;
    private volatile bool _isPaused;
    private string? _pausedBy;
    private DateTime? _pausedAtUtc;
    private string? _pauseReason;

    public SchedulerKillSwitch(IAuditLogger? audit = null)
    {
        _audit = audit;
    }

    public bool IsPaused => _isPaused;
    public string? PausedBy => _pausedBy;
    public DateTime? PausedAtUtc => _pausedAtUtc;
    public string? PauseReason => _pauseReason;

    public async Task PauseAsync(string actor, string? reason, CancellationToken ct = default)
    {
        if (_isPaused) return;
        _isPaused = true;
        _pausedBy = actor;
        _pausedAtUtc = DateTime.UtcNow;
        _pauseReason = reason;

        if (_audit is not null)
        {
            await _audit.LogAsync(
                AuditCategory.Scheduler, AuditAction.Schedule,
                $"⛔ 排程器全域暫停（操作者：{actor}，理由：{reason ?? "未提供"}）",
                actor: actor,
                severity: AuditSeverity.Warning, ct: ct);
        }
    }

    public async Task ResumeAsync(string actor, CancellationToken ct = default)
    {
        if (!_isPaused) return;
        var pausedFor = _pausedAtUtc.HasValue ? (DateTime.UtcNow - _pausedAtUtc.Value).TotalMinutes : 0;
        _isPaused = false;
        _pausedBy = null;
        _pausedAtUtc = null;
        _pauseReason = null;

        if (_audit is not null)
        {
            await _audit.LogAsync(
                AuditCategory.Scheduler, AuditAction.Schedule,
                $"✅ 排程器恢復執行（操作者：{actor}，暫停 {pausedFor:0} 分鐘）",
                actor: actor,
                severity: AuditSeverity.Info, ct: ct);
        }
    }
}
