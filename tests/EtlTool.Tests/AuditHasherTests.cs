using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class AuditHasherTests
{
    private static AuditEvent NewEvent(
        string message = "test",
        AuditCategory cat = AuditCategory.Run,
        AuditAction act = AuditAction.RunSucceeded,
        AuditSeverity sev = AuditSeverity.Info,
        string? actor = "system",
        string? target = "EtlTask",
        Guid? targetId = null,
        string? targetName = "T",
        string? details = null,
        DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        At = at ?? new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc),
        Category = cat,
        Action = act,
        Severity = sev,
        Actor = actor,
        TargetType = target,
        TargetId = targetId,
        TargetName = targetName,
        Message = message,
        DetailsJson = details,
    };

    [Fact]
    public void Same_inputs_give_same_hash()
    {
        var e1 = NewEvent();
        var e2 = NewEvent();
        // Id 不影響 hash（不在編碼欄位內）— 這是刻意的，方便 replay 與測試
        Assert.Equal(AuditHasher.ComputeHash(e1, "PREV"),
                     AuditHasher.ComputeHash(e2, "PREV"));
    }

    [Fact]
    public void Hash_is_64_uppercase_hex_chars()
    {
        var hash = AuditHasher.ComputeHash(NewEvent(), null);
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is >= '0' and <= '9' or >= 'A' and <= 'F',
            $"Non-hex character: {c}"));
    }

    [Fact]
    public void Different_previous_hash_changes_result()
    {
        var e = NewEvent();
        var h1 = AuditHasher.ComputeHash(e, null);
        var h2 = AuditHasher.ComputeHash(e, "ABCDEF");
        var h3 = AuditHasher.ComputeHash(e, "FEDCBA");
        Assert.NotEqual(h1, h2);
        Assert.NotEqual(h2, h3);
    }

    [Theory]
    [InlineData(nameof(AuditEvent.Message))]
    [InlineData(nameof(AuditEvent.Category))]
    [InlineData(nameof(AuditEvent.Action))]
    [InlineData(nameof(AuditEvent.Severity))]
    [InlineData(nameof(AuditEvent.Actor))]
    [InlineData(nameof(AuditEvent.TargetType))]
    [InlineData(nameof(AuditEvent.TargetName))]
    [InlineData(nameof(AuditEvent.DetailsJson))]
    [InlineData(nameof(AuditEvent.At))]
    public void Mutating_any_critical_field_changes_hash(string field)
    {
        var orig = NewEvent();
        var origHash = AuditHasher.ComputeHash(orig, null);

        var mutated = NewEvent();
        switch (field)
        {
            case nameof(AuditEvent.Message):     mutated.Message = "TAMPERED"; break;
            case nameof(AuditEvent.Category):    mutated.Category = AuditCategory.System; break;
            case nameof(AuditEvent.Action):      mutated.Action = AuditAction.RunFailed; break;
            case nameof(AuditEvent.Severity):    mutated.Severity = AuditSeverity.Error; break;
            case nameof(AuditEvent.Actor):       mutated.Actor = "evil"; break;
            case nameof(AuditEvent.TargetType):  mutated.TargetType = "Other"; break;
            case nameof(AuditEvent.TargetName):  mutated.TargetName = "X"; break;
            case nameof(AuditEvent.DetailsJson): mutated.DetailsJson = "{tampered:true}"; break;
            case nameof(AuditEvent.At):          mutated.At = mutated.At.AddSeconds(1); break;
        }

        var newHash = AuditHasher.ComputeHash(mutated, null);
        Assert.NotEqual(origHash, newHash);
    }

    [Fact]
    public void TargetId_change_changes_hash()
    {
        var orig = NewEvent(targetId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var mut  = NewEvent(targetId: Guid.Parse("22222222-2222-2222-2222-222222222222"));
        Assert.NotEqual(
            AuditHasher.ComputeHash(orig, null),
            AuditHasher.ComputeHash(mut, null));
    }

    [Fact]
    public void Null_vs_empty_strings_treated_same()
    {
        // Both should produce same hash (we coerce null → "")
        var withNull  = NewEvent(actor: null);
        var withEmpty = NewEvent(actor: "");
        Assert.Equal(
            AuditHasher.ComputeHash(withNull,  null),
            AuditHasher.ComputeHash(withEmpty, null));
    }

    [Fact]
    public void Chain_dependency_demonstration()
    {
        // 模擬一條 3 筆鏈
        var e1 = NewEvent("first");
        var e2 = NewEvent("second", at: e1.At.AddSeconds(1));
        var e3 = NewEvent("third",  at: e1.At.AddSeconds(2));

        var h1 = AuditHasher.ComputeHash(e1, null);
        var h2 = AuditHasher.ComputeHash(e2, h1);
        var h3 = AuditHasher.ComputeHash(e3, h2);

        // 把 e1 改了 → h1 改 → 鏈裡 h2 / h3 都需重算
        e1.Message = "tampered";
        var h1New = AuditHasher.ComputeHash(e1, null);
        Assert.NotEqual(h1, h1New);
        // 即使 e2 / e3 沒被改，新 prev 算出的鏈跟原本不一樣
        var h2New = AuditHasher.ComputeHash(e2, h1New);
        Assert.NotEqual(h2, h2New);
    }
}
