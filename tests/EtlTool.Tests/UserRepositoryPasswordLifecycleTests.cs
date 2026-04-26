using EtlTool.Core.Engine;
using EtlTool.Core.Models;
using EtlTool.Data;
using EtlTool.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Tests;

/// <summary>
/// Unit tests for UserRepository password lifecycle logic using SQLite in-memory.
/// Uses a fake hash/verify pair to avoid BCrypt cost in unit tests.
/// </summary>
public sealed class UserRepositoryPasswordLifecycleTests : IAsyncLifetime
{
    // Fake hash/verify — fast, deterministic, no BCrypt cost
    private static string Hash(string p) => "h:" + p;
    private static bool Verify(string p, string h) => h == "h:" + p;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private RecordingAuditLogger _audit = null!;
    private UserRepository _repo = null!;

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
        _repo = new UserRepository(_db, _audit);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Task<User> CreateUserAsync(string username = "alice", string password = "InitialPass1!")
    {
        var user = new User
        {
            Username = username,
            PasswordHash = Hash(password),
            Role = UserRole.Viewer,
            IsActive = true,
        };
        return _repo.CreateAsync(user, "admin", default);
    }

    // -----------------------------------------------------------------------
    // 1. CreateAsync sets MustChangePassword=true and LastPasswordChangedAt
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_sets_MustChangePassword_true_and_LastPasswordChangedAt()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var user = await CreateUserAsync();
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.True(user.MustChangePassword);
        Assert.InRange(user.LastPasswordChangedAt, before, after);
    }

    // -----------------------------------------------------------------------
    // 2. CreateAsync writes first password history row
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_writes_first_password_history_row()
    {
        var user = await CreateUserAsync();

        var history = await _db.PasswordHistories
            .Where(h => h.UserId == user.Id)
            .ToListAsync();

        Assert.Single(history);
        Assert.Equal(user.PasswordHash, history[0].PasswordHash);
    }

    // -----------------------------------------------------------------------
    // 3. ResetPasswordAsync sets MustChangePassword=true and appends history
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResetPasswordAsync_sets_MustChangePassword_true_and_appends_history()
    {
        var user = await CreateUserAsync();
        // Clear MustChangePassword to verify Reset sets it back
        user.MustChangePassword = false;
        await _db.SaveChangesAsync();

        var historyBefore = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);

        await _repo.ResetPasswordAsync(user.Id, Hash("NewTemp1!"), "admin", default);

        var refreshed = await _db.Users.FindAsync(user.Id);
        Assert.True(refreshed!.MustChangePassword);

        var historyAfter = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);
        Assert.Equal(historyBefore + 1, historyAfter);

        // Audit entry should be Warning severity
        var lastAudit = _audit.Entries[^1];
        Assert.Equal(AuditCategory.Auth, lastAudit.Category);
        Assert.Equal(AuditSeverity.Warning, lastAudit.Severity);
    }

    // -----------------------------------------------------------------------
    // 4. ChangeOwnPasswordWithReuseCheckAsync rejects reuse of current hash
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_rejects_reuse_of_current_hash()
    {
        var user = await CreateUserAsync(password: "InitialPass1!");

        var auditCountBefore = _audit.Entries.Count;
        var historyBefore = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);

        // Try to "change" to the same plaintext that is already the current hash
        var (success, error) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            user.Id, "InitialPass1!", Hash, Verify, "alice", default);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("歷史密碼", error!); // mentions reuse (不可與最近...歷史密碼相同)

        // No history row should have been added
        var historyAfter = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);
        Assert.Equal(historyBefore, historyAfter);

        // No new audit event
        Assert.Equal(auditCountBefore, _audit.Entries.Count);
    }

    // -----------------------------------------------------------------------
    // 5. ChangeOwnPasswordWithReuseCheckAsync rejects reuse of recent history
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_rejects_reuse_of_recent_history()
    {
        var user = await CreateUserAsync(password: "pwd1StrongXX!");

        // Change through pwd2 → pwd3 → pwd4 → pwd5 → pwd6 (pwd1 is history[0])
        var passwords = new[] { "pwd2StrongXX!", "pwd3StrongXX!", "pwd4StrongXX!", "pwd5StrongXX!", "pwd6StrongXX!" };
        foreach (var pwd in passwords)
        {
            await Task.Delay(2); // ensure monotonic CreatedAt
            var r = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
                user.Id, pwd, Hash, Verify, "alice", default);
            Assert.True(r.Success, $"Expected success when changing to {pwd}");
        }

        // pwd2 should be within last 5 (positions 5,4,3,2,1 from newest = pwd6,pwd5,pwd4,pwd3,pwd2)
        var (success, error) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            user.Id, "pwd2StrongXX!", Hash, Verify, "alice", default);

        Assert.False(success);
        Assert.NotNull(error);
    }

    // -----------------------------------------------------------------------
    // 6. ChangeOwnPasswordWithReuseCheckAsync allows reuse after history pruned
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_allows_reuse_after_history_pruned()
    {
        var user = await CreateUserAsync(password: "pwd1StrongXX!");

        // Change 6 more times so pwd1 is pushed out of the last-5 window
        var passwords = new[]
        {
            "pwd2StrongXX!", "pwd3StrongXX!", "pwd4StrongXX!",
            "pwd5StrongXX!", "pwd6StrongXX!", "pwd7StrongXX!"
        };
        foreach (var pwd in passwords)
        {
            await Task.Delay(2);
            var r = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
                user.Id, pwd, Hash, Verify, "alice", default);
            Assert.True(r.Success, $"Expected success when changing to {pwd}");
        }

        // Now pwd1 should be pruned (only last 5 kept: pwd3..pwd7)
        // Current = pwd7, history = pwd7,pwd6,pwd5,pwd4,pwd3 → pwd1 is gone
        var (success, error) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            user.Id, "pwd1StrongXX!", Hash, Verify, "alice", default);

        Assert.True(success, $"Expected pwd1 to be allowed after pruning; error: {error}");
    }

    // -----------------------------------------------------------------------
    // 7. ChangeOwnPasswordWithReuseCheckAsync clears MustChangePassword
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_clears_MustChangePassword()
    {
        var user = await CreateUserAsync();
        Assert.True(user.MustChangePassword); // set by CreateAsync

        var (success, _) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            user.Id, "NewStrong1!Pass", Hash, Verify, "alice", default);

        Assert.True(success);

        var refreshed = await _db.Users.FindAsync(user.Id);
        Assert.False(refreshed!.MustChangePassword);
    }

    // -----------------------------------------------------------------------
    // 8. ChangeOwnPasswordWithReuseCheckAsync updates LastPasswordChangedAt
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_updates_LastPasswordChangedAt()
    {
        var user = await CreateUserAsync();
        var original = user.LastPasswordChangedAt;

        await Task.Delay(10); // ensure timestamp moves forward

        var (success, _) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            user.Id, "BrandNew1!Pass", Hash, Verify, "alice", default);

        Assert.True(success);

        var refreshed = await _db.Users.FindAsync(user.Id);
        Assert.True(refreshed!.LastPasswordChangedAt > original,
            $"Expected LastPasswordChangedAt to advance beyond {original}; got {refreshed.LastPasswordChangedAt}");
    }

    // -----------------------------------------------------------------------
    // 9. ChangeOwnPasswordWithReuseCheckAsync returns false for unknown user
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_returns_false_for_unknown_user()
    {
        var (success, error) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            Guid.NewGuid(), "SomePass1!XX", Hash, Verify, "ghost", default);

        Assert.False(success);
        Assert.NotNull(error);
    }

    // -----------------------------------------------------------------------
    // 10. ChangeOwnPasswordWithReuseCheckAsync returns false for empty password
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeOwnPasswordWithReuseCheckAsync_returns_false_for_empty_password()
    {
        var user = await CreateUserAsync();

        var (success, error) = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
            user.Id, "", Hash, Verify, "alice", default);

        Assert.False(success);
        Assert.NotNull(error);
    }

    // -----------------------------------------------------------------------
    // 11. DeleteAsync also deletes password history
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_also_deletes_password_history()
    {
        var user = await CreateUserAsync(); // 1 history row

        // Reset 2 more times to accumulate 3 history rows total
        await Task.Delay(2);
        await _repo.ResetPasswordAsync(user.Id, Hash("Reset1!Pass"), "admin", default);
        await Task.Delay(2);
        await _repo.ResetPasswordAsync(user.Id, Hash("Reset2!Pass"), "admin", default);

        var historyBefore = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);
        Assert.Equal(3, historyBefore);

        await _repo.DeleteAsync(user.Id, "admin", default);

        var historyAfter = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);
        Assert.Equal(0, historyAfter);
    }

    // -----------------------------------------------------------------------
    // 12. PruneHistory keeps at most 5 rows
    // BUG: PruneHistoryAsync calls ExecuteDeleteAsync BEFORE SaveChangesAsync,
    // so the newly-added PasswordHistory row is not yet in the DB when pruning
    // queries it. As a result, after the (depth+1)-th change the final count
    // is depth+1 instead of depth. This test is intentionally red to document
    // the off-by-one in PruneHistoryAsync.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PruneHistory_keeps_at_most_5_rows()
    {
        var user = await CreateUserAsync(password: "pwd1StrongXX!"); // history count = 1

        // Change 6 more times; after each ChangeOwn the pruner fires, keeping depth=5
        var extra = new[]
        {
            "pwd2StrongXX!", "pwd3StrongXX!", "pwd4StrongXX!",
            "pwd5StrongXX!", "pwd6StrongXX!", "pwd7StrongXX!"
        };
        foreach (var pwd in extra)
        {
            await Task.Delay(2);
            var r = await _repo.ChangeOwnPasswordWithReuseCheckAsync(
                user.Id, pwd, Hash, Verify, "alice", default);
            Assert.True(r.Success, $"Expected success when changing to {pwd}");
        }

        var count = await _db.PasswordHistories.CountAsync(h => h.UserId == user.Id);
        // Correct expected behavior: depth=5. Currently FAILS (actual=6) due to prune
        // running before SaveChanges — the in-flight new row is invisible to ExecuteDeleteAsync.
        Assert.Equal(UserRepository.PasswordHistoryDepth, count);
    }
}
