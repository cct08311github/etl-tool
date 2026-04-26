using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Tests;

/// <summary>
/// Recording stub for IAuditLogger — captures every LogAsync call so tests can assert on it.
/// </summary>
internal sealed class RecordingAuditLogger : IAuditLogger
{
    public record Entry(
        AuditCategory Category,
        AuditAction Action,
        string Message,
        string? TargetType,
        Guid? TargetId,
        string? TargetName,
        AuditSeverity Severity,
        string? DetailsJson,
        string? Actor);

    public List<Entry> Entries { get; } = new();

    public Task LogAsync(
        AuditCategory category,
        AuditAction action,
        string message,
        string? targetType = null,
        Guid? targetId = null,
        string? targetName = null,
        AuditSeverity severity = AuditSeverity.Info,
        string? detailsJson = null,
        string? actor = null,
        CancellationToken ct = default)
    {
        Entries.Add(new Entry(category, action, message, targetType, targetId, targetName, severity, detailsJson, actor));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Unit tests for ApprovalRepository using SQLite in-memory so ExpiresAt comparisons behave like prod.
/// Each test gets a fresh connection + DbContext + logger instance.
/// </summary>
public sealed class ApprovalRepositoryTests : IAsyncLifetime
{
    // One open SqliteConnection per test instance — keeps :memory: DB alive for the lifetime of the test.
    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private RecordingAuditLogger _audit = null!;
    private ApprovalRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _audit = new RecordingAuditLogger();
        _repo = new ApprovalRepository(_db, _audit);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly Guid _targetId = Guid.NewGuid();
    private const string TargetType = "EtlTask";
    private const string TargetName = "My Task";
    private const string UserAlice = "alice";
    private const string UserBob = "bob";

    private Task<ApprovalRequest> SubmitAliceAsync(
        string? overrideTargetType = null,
        Guid? overrideTargetId = null,
        string? submittedBy = UserAlice) =>
        _repo.SubmitAsync(
            ApprovalAction.DeleteTask,
            overrideTargetType ?? TargetType,
            overrideTargetId ?? _targetId,
            TargetName,
            submittedBy ?? UserAlice,
            "test reason",
            default);

    // -----------------------------------------------------------------------
    // 1. SubmitAsync — happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SubmitAsync_creates_pending_request()
    {
        var before = DateTime.UtcNow;
        var req = await SubmitAliceAsync();
        var after = DateTime.UtcNow;

        Assert.NotEqual(Guid.Empty, req.Id);
        Assert.Equal(ApprovalStatus.Pending, req.Status);
        Assert.Equal(UserAlice, req.SubmittedBy);

        // ExpiresAt should be roughly 7 days from now (allow a 5-second window around the test run)
        Assert.InRange(req.ExpiresAt,
            before.AddDays(7).AddSeconds(-5),
            after.AddDays(7).AddSeconds(5));

        // Audit logged once with category=Auth and severity=Warning
        Assert.Single(_audit.Entries);
        var entry = _audit.Entries[0];
        Assert.Equal(AuditCategory.Auth, entry.Category);
        Assert.Equal(AuditSeverity.Warning, entry.Severity);
        Assert.Equal(UserAlice, entry.Actor);
    }

    // -----------------------------------------------------------------------
    // 2. SubmitAsync — rejects duplicate pending for same target
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SubmitAsync_rejects_when_pending_exists_for_same_target()
    {
        var first = await SubmitAliceAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.SubmitAsync(
                ApprovalAction.DeleteTask,
                TargetType, _targetId, TargetName,
                UserBob, "second attempt", default));

        // Message must mention the original submitter
        Assert.Contains(first.SubmittedBy, ex.Message);
    }

    // -----------------------------------------------------------------------
    // 3. SubmitAsync — allows new pending after previous was Rejected
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SubmitAsync_allows_new_pending_after_previous_rejected()
    {
        var first = await SubmitAliceAsync();
        // Reject the first request
        await _repo.RejectAsync(first.Id, UserBob, "no", default);

        // Now a new submission for the same target should succeed
        var second = await _repo.SubmitAsync(
            ApprovalAction.DeleteTask,
            TargetType, _targetId, TargetName,
            UserAlice, "retry", default);

        Assert.Equal(ApprovalStatus.Pending, second.Status);
    }

    // -----------------------------------------------------------------------
    // 4. SubmitAsync — allows new pending after previous expired
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SubmitAsync_allows_new_pending_after_previous_expired()
    {
        var first = await SubmitAliceAsync();

        // Manually backdate ExpiresAt so it reads as already expired
        first.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        // A new submission for the same target should now succeed
        var second = await _repo.SubmitAsync(
            ApprovalAction.DeleteTask,
            TargetType, _targetId, TargetName,
            UserAlice, "retry after expiry", default);

        Assert.Equal(ApprovalStatus.Pending, second.Status);
    }

    // -----------------------------------------------------------------------
    // 5. ApproveAsync — throws on self-approval (same username, Ordinal)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApproveAsync_throws_on_self_approval()
    {
        var req = await SubmitAliceAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.ApproveAsync(req.Id, UserAlice, null, default));

        // Message must contain the Chinese two-man rule text
        Assert.Contains("不可自我核准", ex.Message);
    }

    // -----------------------------------------------------------------------
    // 6. ApproveAsync — succeeds when different user
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApproveAsync_succeeds_when_different_user()
    {
        var req = await SubmitAliceAsync();
        var approved = await _repo.ApproveAsync(req.Id, UserBob, "looks good", default);

        Assert.Equal(ApprovalStatus.Approved, approved.Status);
        Assert.Equal(UserBob, approved.DecidedBy);
        Assert.NotNull(approved.DecidedAt);
    }

    // -----------------------------------------------------------------------
    // 7. ApproveAsync — throws when not Pending
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApproveAsync_throws_when_not_pending()
    {
        var req = await SubmitAliceAsync();
        // First approve it
        await _repo.ApproveAsync(req.Id, UserBob, null, default);

        // Second approve attempt must throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.ApproveAsync(req.Id, UserBob, null, default));

        Assert.Contains(ApprovalStatus.Approved.ToString(), ex.Message);
    }

    // -----------------------------------------------------------------------
    // 8. ApproveAsync — marks Expired and throws when past expiry
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ApproveAsync_marks_expired_and_throws_when_past_expiry()
    {
        var req = await SubmitAliceAsync();

        // Manually backdate ExpiresAt so it is in the past
        req.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.ApproveAsync(req.Id, UserBob, null, default));

        Assert.Contains("過期", ex.Message);

        // Status must have been persisted as Expired
        var refreshed = await _db.ApprovalRequests.FindAsync(req.Id);
        Assert.Equal(ApprovalStatus.Expired, refreshed!.Status);
    }

    // -----------------------------------------------------------------------
    // 9. RejectAsync — succeeds and audits with Info severity
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RejectAsync_succeeds_and_audits_with_info_severity()
    {
        var req = await SubmitAliceAsync();
        var auditCountBefore = _audit.Entries.Count;

        var rejected = await _repo.RejectAsync(req.Id, UserBob, "not now", default);

        Assert.Equal(ApprovalStatus.Rejected, rejected.Status);
        Assert.Equal(UserBob, rejected.DecidedBy);
        Assert.Equal("not now", rejected.DecisionReason);

        // Exactly one new audit entry was added
        Assert.Equal(auditCountBefore + 1, _audit.Entries.Count);
        var entry = _audit.Entries[^1];
        Assert.Equal(AuditSeverity.Info, entry.Severity);
        Assert.Equal(UserBob, entry.Actor);
    }

    // -----------------------------------------------------------------------
    // 10. RejectAsync — throws when not Pending
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RejectAsync_throws_when_not_pending()
    {
        var req = await SubmitAliceAsync();
        // Approve first so it is no longer Pending
        await _repo.ApproveAsync(req.Id, UserBob, null, default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.RejectAsync(req.Id, UserBob, null, default));

        Assert.Contains(ApprovalStatus.Approved.ToString(), ex.Message);
    }

    // -----------------------------------------------------------------------
    // 11. CountPendingAsync — excludes expired and decided requests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CountPendingAsync_excludes_expired_and_decided()
    {
        // 1) Fresh pending — counts
        await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());

        // 2) Pending but manually expired — does NOT count
        var expiredPending = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());
        expiredPending.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        // 3) Approved — does NOT count
        var toApprove = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());
        await _repo.ApproveAsync(toApprove.Id, UserBob, null, default);

        // 4) Rejected — does NOT count
        var toReject = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());
        await _repo.RejectAsync(toReject.Id, UserBob, null, default);

        var count = await _repo.CountPendingAsync(default);
        Assert.Equal(1, count);
    }

    // -----------------------------------------------------------------------
    // 12. ListPendingAsync — ordered by SubmittedAt desc, excludes expired
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListPendingAsync_orders_by_submitted_desc_and_excludes_expired()
    {
        // Submit three fresh pending requests (slight delay between each to get distinct timestamps)
        var r1 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());
        var r2 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());
        var r3 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid());

        // Expire r2
        r2.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var list = await _repo.ListPendingAsync(default);

        // Expired r2 must not appear
        Assert.DoesNotContain(list, x => x.Id == r2.Id);

        // r1 and r3 must appear, ordered descending by SubmittedAt
        // (r3 was submitted after r1, so it should come first)
        Assert.Contains(list, x => x.Id == r1.Id);
        Assert.Contains(list, x => x.Id == r3.Id);

        for (int i = 0; i < list.Count - 1; i++)
            Assert.True(list[i].SubmittedAt >= list[i + 1].SubmittedAt,
                "List should be ordered by SubmittedAt descending");
    }

    // -----------------------------------------------------------------------
    // 13. ListRecentAsync — returns N most recent regardless of status
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListRecentAsync_returns_n_most_recent_regardless_of_status()
    {
        // Create requests in different states
        var r1 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid()); // pending
        var r2 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid()); // approved
        await _repo.ApproveAsync(r2.Id, UserBob, null, default);
        var r3 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid()); // rejected
        await _repo.RejectAsync(r3.Id, UserBob, null, default);
        var r4 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid()); // expired
        r4.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();
        var r5 = await SubmitAliceAsync(overrideTargetId: Guid.NewGuid()); // pending fresh

        // Request 3 most recent — all statuses should be eligible
        var top3 = await _repo.ListRecentAsync(3, default);

        Assert.Equal(3, top3.Count);

        // The 3 most recently submitted should be r3, r4, r5 (r1 and r2 were earlier).
        // Regardless of status, all must be represented.
        var ids = top3.Select(x => x.Id).ToHashSet();
        Assert.Contains(r5.Id, ids);
        Assert.Contains(r4.Id, ids);
        Assert.Contains(r3.Id, ids);
        Assert.DoesNotContain(r1.Id, ids);
        Assert.DoesNotContain(r2.Id, ids);
    }
}
