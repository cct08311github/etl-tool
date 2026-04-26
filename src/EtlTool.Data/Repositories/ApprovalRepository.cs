using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data.Repositories;

/// <summary>
/// 兩人覆核請求的 CRUD + decision 操作。
///
/// 重點規則：
///   1) 不可自我核准：DecidedBy 必須 != SubmittedBy
///   2) 只能對 Pending 做 Approve/Reject；過期 / 已決定的不可改
///   3) 同一個 target 同時只能有一個 Pending 請求（防止重複提交）
///   4) 所有狀態變更都寫 audit
/// </summary>
public sealed class ApprovalRepository
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;

    public ApprovalRepository(AppDbContext db, IAuditLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public Task<List<ApprovalRequest>> ListPendingAsync(CancellationToken ct)
        => _db.ApprovalRequests
            .AsNoTracking()
            .Where(r => r.Status == ApprovalStatus.Pending && r.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(r => r.SubmittedAt)
            .ToListAsync(ct);

    public Task<int> CountPendingAsync(CancellationToken ct)
        => _db.ApprovalRequests
            .Where(r => r.Status == ApprovalStatus.Pending && r.ExpiresAt > DateTime.UtcNow)
            .CountAsync(ct);

    public Task<List<ApprovalRequest>> ListRecentAsync(int take, CancellationToken ct)
        => _db.ApprovalRequests
            .AsNoTracking()
            .OrderByDescending(r => r.SubmittedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task<ApprovalRequest> SubmitAsync(
        ApprovalAction action,
        string targetType, Guid targetId, string targetName,
        string submittedBy, string? reason,
        CancellationToken ct)
    {
        // 不可重複提交（同 target 已有 pending 就拒絕）
        var existing = await _db.ApprovalRequests
            .Where(r => r.TargetType == targetType
                     && r.TargetId == targetId
                     && r.Status == ApprovalStatus.Pending
                     && r.ExpiresAt > DateTime.UtcNow)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            throw new InvalidOperationException(
                $"已有 pending 中的核准請求（提交者 {existing.SubmittedBy}，{existing.SubmittedAt.ToLocalTime():yyyy-MM-dd HH:mm}）。請等核准或撤銷後再提交。");

        var req = new ApprovalRequest
        {
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            SubmittedBy = submittedBy,
            SubmittedAt = DateTime.UtcNow,
            SubmissionReason = reason,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Status = ApprovalStatus.Pending,
        };
        _db.ApprovalRequests.Add(req);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"提交{ActionLabel(action)}請求：「{targetName}」(等待第二人核准)",
            targetType: targetType, targetId: targetId, targetName: targetName,
            severity: AuditSeverity.Warning, actor: submittedBy,
            detailsJson: System.Text.Json.JsonSerializer.Serialize(new { requestId = req.Id, reason }),
            ct: ct);

        return req;
    }

    public async Task<ApprovalRequest> ApproveAsync(Guid requestId, string approver, string? reason, CancellationToken ct)
    {
        var req = await _db.ApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException("請求不存在");

        if (req.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"請求狀態為 {req.Status}，無法再核准");

        if (req.ExpiresAt <= DateTime.UtcNow)
        {
            req.Status = ApprovalStatus.Expired;
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("請求已過期");
        }

        if (string.Equals(req.SubmittedBy, approver, StringComparison.Ordinal))
            throw new InvalidOperationException("⛔ 不可自我核准（two-man rule）— 請由不同帳號的 Admin 核准");

        req.Status = ApprovalStatus.Approved;
        req.DecidedBy = approver;
        req.DecidedAt = DateTime.UtcNow;
        req.DecisionReason = reason;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"✓ 核准 {ActionLabel(req.Action)}：「{req.TargetName}」(由 {approver} 核准；提交者 {req.SubmittedBy})",
            targetType: req.TargetType, targetId: req.TargetId, targetName: req.TargetName,
            severity: AuditSeverity.Warning, actor: approver, ct: ct);

        return req;
    }

    public async Task<ApprovalRequest> RejectAsync(Guid requestId, string rejector, string? reason, CancellationToken ct)
    {
        var req = await _db.ApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException("請求不存在");

        if (req.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException($"請求狀態為 {req.Status}");

        req.Status = ApprovalStatus.Rejected;
        req.DecidedBy = rejector;
        req.DecidedAt = DateTime.UtcNow;
        req.DecisionReason = reason;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditCategory.Auth, AuditAction.Update,
            $"✗ 拒絕 {ActionLabel(req.Action)}：「{req.TargetName}」(由 {rejector} 拒絕)",
            targetType: req.TargetType, targetId: req.TargetId, targetName: req.TargetName,
            severity: AuditSeverity.Info, actor: rejector, ct: ct);

        return req;
    }

    private static string ActionLabel(ApprovalAction a) => a switch
    {
        ApprovalAction.DeleteConnection => "刪除連線",
        ApprovalAction.DeleteTask => "刪除任務",
        _ => a.ToString(),
    };
}
