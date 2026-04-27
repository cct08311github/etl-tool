using EtlTool.App.Auth;

namespace EtlTool.Tests;

public class ApiKeyAuthServiceTests
{
    [Fact]
    public void Empty_keys_disables_auth()
    {
        var svc = new ApiKeyAuthService(Array.Empty<string>());
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public void Whitespace_only_entries_filtered()
    {
        var svc = new ApiKeyAuthService(new[] { "  ", "", " " });
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public void Valid_key_passes_check()
    {
        var svc = new ApiKeyAuthService(new[] { "secret-key-1" });
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsValid("secret-key-1"));
    }

    [Fact]
    public void Trailing_whitespace_in_provided_is_trimmed()
    {
        var svc = new ApiKeyAuthService(new[] { "secret" });
        Assert.True(svc.IsValid("  secret  "));
    }

    [Fact]
    public void Invalid_key_rejected()
    {
        var svc = new ApiKeyAuthService(new[] { "secret-key-1" });
        Assert.False(svc.IsValid("wrong-key"));
    }

    [Fact]
    public void Empty_or_null_provided_rejected()
    {
        var svc = new ApiKeyAuthService(new[] { "secret-key-1" });
        Assert.False(svc.IsValid(null));
        Assert.False(svc.IsValid(""));
        Assert.False(svc.IsValid("   "));
    }

    [Fact]
    public void Multiple_keys_any_match_passes()
    {
        var svc = new ApiKeyAuthService(new[] { "k1", "k2", "k3" });
        Assert.True(svc.IsValid("k1"));
        Assert.True(svc.IsValid("k2"));
        Assert.True(svc.IsValid("k3"));
        Assert.False(svc.IsValid("k4"));
    }

    [Fact]
    public void Case_sensitive_compare()
    {
        var svc = new ApiKeyAuthService(new[] { "SecretKey" });
        Assert.True(svc.IsValid("SecretKey"));
        Assert.False(svc.IsValid("secretkey"));
        Assert.False(svc.IsValid("SECRETKEY"));
    }

    [Fact]
    public void Disabled_service_rejects_everything()
    {
        var svc = new ApiKeyAuthService(Array.Empty<string>());
        Assert.False(svc.IsValid("anything"));
    }

    [Fact]
    public void Duplicate_keys_dedupe()
    {
        // Just confirms ctor doesn't crash on duplicates and IsValid still works
        var svc = new ApiKeyAuthService(new[] { "k1", "k1", "k1" });
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsValid("k1"));
        // Dedup → KeyCount should be 1, not 3
        Assert.Equal(1, svc.KeyCount);
    }

    [Fact]
    public void KeyCount_reflects_distinct_keys()
    {
        Assert.Equal(0, new ApiKeyAuthService(Array.Empty<string>()).KeyCount);
        Assert.Equal(3, new ApiKeyAuthService(new[] { "a", "b", "c" }).KeyCount);
        // Whitespace-only entries are filtered before counting
        Assert.Equal(2, new ApiKeyAuthService(new[] { "x", "  ", "y", "" }).KeyCount);
    }

    [Fact]
    public void KeyFingerprints_are_short_hex_and_match_count()
    {
        var svc = new ApiKeyAuthService(new[] { "alpha", "beta" });
        var fps = svc.KeyFingerprints;
        Assert.Equal(2, fps.Count);
        // 4 bytes → 8 hex chars
        Assert.All(fps, fp =>
        {
            Assert.Equal(8, fp.Length);
            Assert.Matches(@"^[0-9a-f]{8}$", fp);
        });
        // Different inputs → different fingerprints (collisions on first 4 bytes are vanishingly rare for SHA-256)
        Assert.NotEqual(fps[0], fps[1]);
    }

    [Fact]
    public void KeyFingerprints_stable_across_construction()
    {
        // Same input → same fingerprint each time (deterministic SHA-256)
        var a = new ApiKeyAuthService(new[] { "secret" });
        var b = new ApiKeyAuthService(new[] { "secret" });
        Assert.Equal(a.KeyFingerprints[0], b.KeyFingerprints[0]);
    }
}
