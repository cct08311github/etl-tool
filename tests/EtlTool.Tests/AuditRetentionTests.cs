using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class AuditRetentionTests
{
    private static readonly DateTime Now = new DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc);

    private static AuditEvent MakeEvent(DateTime at, AuditCategory category = AuditCategory.Run)
        => new AuditEvent { Id = Guid.NewGuid(), At = at, Category = category, Action = AuditAction.RunSucceeded, Message = "test" };

    // Case 1: Both policy fields null → return empty list (delete nothing)
    [Fact]
    public void BothNull_ReturnsEmpty()
    {
        var events = new[]
        {
            MakeEvent(Now.AddDays(-100)),
            MakeEvent(Now.AddDays(-1)),
        };
        var policy = new AuditRetentionPolicy(null, null);
        var result = AuditRetention.SelectIdsToDelete(events, policy, Now);
        Assert.Empty(result);
    }

    // Case 2: KeepDays=7 with 8-day-old and 6-day-old → only 8-day-old deleted
    [Fact]
    public void KeepDays7_DeletesOldOne()
    {
        var old = MakeEvent(Now.AddDays(-8));
        var recent = MakeEvent(Now.AddDays(-6));
        var policy = new AuditRetentionPolicy(7, null);
        var result = AuditRetention.SelectIdsToDelete(new[] { old, recent }, policy, Now);
        Assert.Equal(new[] { old.Id }, result.OrderBy(x => x).ToArray());
    }

    // Case 3: Boundary — exactly 7 days + 1ms ago → deleted; exactly 7 days ago → kept
    [Fact]
    public void KeepDays7_Boundary()
    {
        var justOver = MakeEvent(Now.AddDays(-7).AddMilliseconds(-1)); // At < cutoff → delete
        var exactBoundary = MakeEvent(Now.AddDays(-7));                // At == cutoff → keep
        var policy = new AuditRetentionPolicy(7, null);
        var result = AuditRetention.SelectIdsToDelete(new[] { justOver, exactBoundary }, policy, Now);
        Assert.Contains(justOver.Id, result);
        Assert.DoesNotContain(exactBoundary.Id, result);
    }

    // Case 4: KeepLastPerCategory=2, 5 events same category → delete 3 oldest
    [Fact]
    public void KeepLastPerCategory2_SameCategory_DeletesThreeOldest()
    {
        var events = Enumerable.Range(1, 5)
            .Select(i => MakeEvent(Now.AddDays(-i), AuditCategory.Run))
            .ToArray();
        // events[0] = 1 day ago (newest), events[4] = 5 days ago (oldest)
        var policy = new AuditRetentionPolicy(null, 2);
        var result = AuditRetention.SelectIdsToDelete(events, policy, Now);
        Assert.Equal(3, result.Count);
        // The 3 oldest (events[2], [3], [4]) should be deleted
        var deletedIds = result.ToHashSet();
        Assert.Contains(events[2].Id, deletedIds);
        Assert.Contains(events[3].Id, deletedIds);
        Assert.Contains(events[4].Id, deletedIds);
        Assert.DoesNotContain(events[0].Id, deletedIds);
        Assert.DoesNotContain(events[1].Id, deletedIds);
    }

    // Case 5: KeepLastPerCategory=2, cross-category (A:3, B:1) → only delete A's oldest, keep B
    [Fact]
    public void KeepLastPerCategory2_CrossCategory()
    {
        var a1 = MakeEvent(Now.AddDays(-1), AuditCategory.Run);
        var a2 = MakeEvent(Now.AddDays(-2), AuditCategory.Run);
        var a3 = MakeEvent(Now.AddDays(-3), AuditCategory.Run);
        var b1 = MakeEvent(Now.AddDays(-5), AuditCategory.Task);
        var policy = new AuditRetentionPolicy(null, 2);
        var result = AuditRetention.SelectIdsToDelete(new[] { a1, a2, a3, b1 }, policy, Now);
        Assert.Single(result);
        Assert.Contains(a3.Id, result);
        Assert.DoesNotContain(b1.Id, result);
    }

    // Case 6: OR semantics — KeepDays=7 + KeepLastPerCategory=2
    //         Event is 30 days old BUT is among the newest 2 in its category → keep (not deleted)
    [Fact]
    public void OrSemantics_OldButInTopN_Kept()
    {
        // Category Run has only 2 events, both old. KeepDays=7 would delete them,
        // but KeepLastPerCategory=2 keeps them.
        var e1 = MakeEvent(Now.AddDays(-30), AuditCategory.Run);
        var e2 = MakeEvent(Now.AddDays(-25), AuditCategory.Run);
        var policy = new AuditRetentionPolicy(7, 2);
        var result = AuditRetention.SelectIdsToDelete(new[] { e1, e2 }, policy, Now);
        Assert.Empty(result);
    }

    // Additional OR test: 3 events in same category, all > 7 days old.
    // KeepDays=7 wants to delete all 3. KeepLastPerCategory=2 keeps top 2.
    // → only the oldest 1 gets deleted.
    [Fact]
    public void OrSemantics_AllOldThreeEvents_OnlyOldestDeleted()
    {
        var e1 = MakeEvent(Now.AddDays(-8), AuditCategory.Run);   // oldest
        var e2 = MakeEvent(Now.AddDays(-10), AuditCategory.Run);
        var e3 = MakeEvent(Now.AddDays(-15), AuditCategory.Run);  // oldest
        var policy = new AuditRetentionPolicy(7, 2);
        var result = AuditRetention.SelectIdsToDelete(new[] { e1, e2, e3 }, policy, Now);
        // e1 and e2 are the top 2 newest → kept by KeepLastPerCategory
        // e3 is not in top 2 and is older than 7 days → deleted
        Assert.Single(result);
        Assert.Contains(e3.Id, result);
    }

    // Case 7: Invalid policy values → ArgumentException
    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    public void InvalidPolicy_ThrowsArgumentException(int? keepDays, int? keepLastPerCategory)
    {
        var policy = new AuditRetentionPolicy(keepDays, keepLastPerCategory);
        Assert.Throws<ArgumentException>(() =>
            AuditRetention.SelectIdsToDelete(Array.Empty<AuditEvent>(), policy, Now));
    }

    // Case 8: Empty events + any valid policy → empty list
    [Theory]
    [InlineData(7, null)]
    [InlineData(null, 3)]
    [InlineData(30, 10)]
    public void EmptyEvents_ReturnsEmpty(int? keepDays, int? keepLastPerCategory)
    {
        var policy = new AuditRetentionPolicy(keepDays, keepLastPerCategory);
        var result = AuditRetention.SelectIdsToDelete(Array.Empty<AuditEvent>(), policy, Now);
        Assert.Empty(result);
    }

    // Case 9: All 6 AuditCategory values — each category 3 events, KeepLastPerCategory=2 → each deletes exactly 1
    [Theory]
    [InlineData(AuditCategory.Connection)]
    [InlineData(AuditCategory.Task)]
    [InlineData(AuditCategory.Run)]
    [InlineData(AuditCategory.Scheduler)]
    [InlineData(AuditCategory.System)]
    [InlineData(AuditCategory.Auth)]
    public void AllCategories_KeepLast2_DeletesOneOldest(AuditCategory category)
    {
        // 3 events in the given category: newest first, oldest last
        var newest = MakeEvent(Now.AddDays(-1), category);
        var middle = MakeEvent(Now.AddDays(-2), category);
        var oldest = MakeEvent(Now.AddDays(-3), category);

        var policy = new AuditRetentionPolicy(null, 2);
        var result = AuditRetention.SelectIdsToDelete(new[] { newest, middle, oldest }, policy, Now);

        Assert.Single(result);
        Assert.Contains(oldest.Id, result);
        Assert.DoesNotContain(newest.Id, result);
        Assert.DoesNotContain(middle.Id, result);
    }

    // Case 10: Tie-breaking — two events with identical At, KeepLastPerCategory=1
    //   Exactly one must be deleted, not both and not neither.
    [Fact]
    public void TieBreaking_SameTimestamp_KeepLast1_ExactlyOneDeleted()
    {
        var sameTime = Now.AddDays(-1);
        var e1 = MakeEvent(sameTime, AuditCategory.Run);
        var e2 = MakeEvent(sameTime, AuditCategory.Run);

        var policy = new AuditRetentionPolicy(null, 1);
        var result = AuditRetention.SelectIdsToDelete(new[] { e1, e2 }, policy, Now);

        // Exactly one gets deleted — not both and not neither
        Assert.Single(result);
        // The survivor must be one of the two
        var deletedId = result[0];
        Assert.True(deletedId == e1.Id || deletedId == e2.Id);
    }

    // Case 11: Single event + KeepLastPerCategory=1 → must be kept (never deleted)
    [Fact]
    public void SingleEvent_KeepLast1_MustBeKept()
    {
        var e = MakeEvent(Now.AddDays(-100), AuditCategory.Run);
        var policy = new AuditRetentionPolicy(null, 1);
        var result = AuditRetention.SelectIdsToDelete(new[] { e }, policy, Now);
        Assert.Empty(result);
    }

    // Case 12: Minimum valid values — KeepDays=1 + KeepLastPerCategory=1
    //   Event 2 days old AND not in top-1 → deleted; event today → kept by KeepDays
    [Fact]
    public void MinimumBoundary_KeepDays1_KeepLast1()
    {
        var todayEvent  = MakeEvent(Now,              AuditCategory.Run);
        var oldEvent1   = MakeEvent(Now.AddDays(-2),  AuditCategory.Run); // older, rank #3
        var oldEvent2   = MakeEvent(Now.AddDays(-3),  AuditCategory.Run); // oldest, rank #3

        var policy = new AuditRetentionPolicy(1, 1);
        var result = AuditRetention.SelectIdsToDelete(
            new[] { todayEvent, oldEvent1, oldEvent2 }, policy, Now);

        // todayEvent: within 1 day → kept by KeepDays; rank #1 → also kept by KeepLast
        // oldEvent1:  > 1 day old, not rank-1 → deleted
        // oldEvent2:  > 1 day old, not rank-1 → deleted
        Assert.Equal(2, result.Count);
        Assert.Contains(oldEvent1.Id, result);
        Assert.Contains(oldEvent2.Id, result);
        Assert.DoesNotContain(todayEvent.Id, result);
    }

    // Case 13: KeepDays large value → all events kept (nothing deleted).
    // Use 365000 (~1000 years back from Now=2026 lands at year 1026, well within DateTime range).
    [Fact]
    public void KeepDaysLargeValue_KeepsAll()
    {
        var events = new[]
        {
            MakeEvent(Now.AddDays(-10000)),
            MakeEvent(Now.AddDays(-50000)),
            MakeEvent(Now.AddYears(-5)),
        };
        var policy = new AuditRetentionPolicy(365000, null);
        var result = AuditRetention.SelectIdsToDelete(events, policy, Now);
        Assert.Empty(result);
    }
}
