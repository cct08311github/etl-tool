using EtlTool.App.Services;

namespace EtlTool.Tests;

/// <summary>
/// 確認 HMAC-SHA256 webhook 簽章是 deterministic 且 receiver 容易實作。
/// 驗 timestamp 防 replay 是 receiver 的責任，這裡只測 signature 本身。
/// </summary>
public class WebhookSignatureTests
{
    [Fact]
    public void Signature_is_deterministic_for_same_inputs()
    {
        var sig1 = HttpFailureNotifier.ComputeSignatureForTesting("topsecret", "1700000000", """{"a":1}""");
        var sig2 = HttpFailureNotifier.ComputeSignatureForTesting("topsecret", "1700000000", """{"a":1}""");
        Assert.Equal(sig1, sig2);
    }

    [Fact]
    public void Signature_changes_with_secret()
    {
        var sigA = HttpFailureNotifier.ComputeSignatureForTesting("secretA", "1700000000", "body");
        var sigB = HttpFailureNotifier.ComputeSignatureForTesting("secretB", "1700000000", "body");
        Assert.NotEqual(sigA, sigB);
    }

    [Fact]
    public void Signature_changes_with_timestamp()
    {
        var sig1 = HttpFailureNotifier.ComputeSignatureForTesting("secret", "1700000000", "body");
        var sig2 = HttpFailureNotifier.ComputeSignatureForTesting("secret", "1700000001", "body");
        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void Signature_changes_with_body()
    {
        var sig1 = HttpFailureNotifier.ComputeSignatureForTesting("secret", "1700000000", "bodyA");
        var sig2 = HttpFailureNotifier.ComputeSignatureForTesting("secret", "1700000000", "bodyB");
        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void Signature_is_64_hex_chars_lowercase()
    {
        var sig = HttpFailureNotifier.ComputeSignatureForTesting("secret", "1700000000", "body");
        Assert.Equal(64, sig.Length);
        Assert.Matches(@"^[0-9a-f]{64}$", sig);
    }

    [Fact]
    public void Signature_known_vector()
    {
        // Reference: HMAC-SHA256("ts=1700000000.body=hello", key="abc")
        // Receiver 實作可拿這個當 fixture 對照。
        var sig = HttpFailureNotifier.ComputeSignatureForTesting("abc", "1700000000", "hello");
        // 用 openssl 驗算過：
        //   echo -n "1700000000.hello" | openssl dgst -sha256 -hmac "abc"
        Assert.Equal("d606dcb8803cf0a7ba1ce6f2dd742366915e55094c36477806a23fe4a709966e", sig);
    }
}
