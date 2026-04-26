using System.Net;
using EtlTool.App.Auth;

namespace EtlTool.Tests;

public class AdminIpAllowlistServiceTests
{
    // ── 1. Empty list disables allowlist ─────────────────────────────────────

    [Fact]
    public void Empty_list_disables_allowlist()
    {
        var svc = new AdminIpAllowlistService(Array.Empty<string>());
        Assert.False(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("1.2.3.4")));
        Assert.True(svc.IsAllowed(IPAddress.Parse("::1")));
    }

    // ── 2. Star entry disables allowlist ─────────────────────────────────────

    [Fact]
    public void Star_entry_disables_allowlist()
    {
        var svc = new AdminIpAllowlistService(["*"]);
        Assert.False(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.0.0.1")));
    }

    // ── 3. Single IPv4 match ──────────────────────────────────────────────────

    [Fact]
    public void Single_ip_match_v4()
    {
        var svc = new AdminIpAllowlistService(["10.0.0.5"]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.0.0.5")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("10.0.0.6")));
    }

    // ── 4. Single IPv6 match ──────────────────────────────────────────────────

    [Fact]
    public void Single_ip_match_v6()
    {
        var svc = new AdminIpAllowlistService(["::1"]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("::1")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("::2")));
    }

    // ── 5. "localhost" resolves loopback ──────────────────────────────────────

    [Fact]
    public void Localhost_keyword_resolves_loopback()
    {
        var svc = new AdminIpAllowlistService(["localhost"]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Loopback));       // 127.0.0.1
        Assert.True(svc.IsAllowed(IPAddress.IPv6Loopback));   // ::1
        Assert.False(svc.IsAllowed(IPAddress.Parse("10.0.0.1")));
    }

    // ── 6. "loopback" keyword works the same ─────────────────────────────────

    [Fact]
    public void Loopback_keyword_works()
    {
        var svc = new AdminIpAllowlistService(["loopback"]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Loopback));
        Assert.True(svc.IsAllowed(IPAddress.IPv6Loopback));
    }

    // ── 7. CIDR /24 ───────────────────────────────────────────────────────────

    [Fact]
    public void Cidr_v4_24()
    {
        var svc = new AdminIpAllowlistService(["192.168.1.0/24"]);
        Assert.True(svc.IsAllowed(IPAddress.Parse("192.168.1.1")));
        Assert.True(svc.IsAllowed(IPAddress.Parse("192.168.1.254")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("192.168.2.0")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("10.0.0.1")));
    }

    // ── 8. CIDR /8 ────────────────────────────────────────────────────────────

    [Fact]
    public void Cidr_v4_8()
    {
        var svc = new AdminIpAllowlistService(["10.0.0.0/8"]);
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.0.0.1")));
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.255.255.255")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("11.0.0.1")));
    }

    // ── 9. IPv6 CIDR ──────────────────────────────────────────────────────────

    [Fact]
    public void Cidr_v6()
    {
        var svc = new AdminIpAllowlistService(["fe80::/10"]);
        Assert.True(svc.IsAllowed(IPAddress.Parse("fe80::1")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("fc00::1")));
    }

    // ── 10. 0.0.0.0/0 allows any v4 ─────────────────────────────────────────

    [Fact]
    public void Cidr_zero_prefix_allows_all()
    {
        var svc = new AdminIpAllowlistService(["0.0.0.0/0"]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("1.2.3.4")));
        Assert.True(svc.IsAllowed(IPAddress.Parse("203.0.113.99")));
    }

    // ── 11. Multiple entries combined ─────────────────────────────────────────

    [Fact]
    public void Multiple_entries_combined()
    {
        var svc = new AdminIpAllowlistService(["10.0.0.0/8", "192.168.1.5", "::1"]);
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.1.2.3")));      // CIDR match
        Assert.True(svc.IsAllowed(IPAddress.Parse("192.168.1.5")));   // exact match
        Assert.True(svc.IsAllowed(IPAddress.IPv6Loopback));           // ::1 match
        Assert.False(svc.IsAllowed(IPAddress.Parse("192.168.1.6"))); // none match
        Assert.False(svc.IsAllowed(IPAddress.Parse("11.0.0.1")));    // outside CIDR
    }

    // ── 12. IPv4-mapped IPv6 normalised to IPv4 ───────────────────────────────

    [Fact]
    public void Mapped_ipv6_normalized_to_v4()
    {
        // Allowlist has IPv4 10.0.0.5; request arrives as IPv4-mapped IPv6
        var svc = new AdminIpAllowlistService(["10.0.0.5"]);
        var mappedV6 = IPAddress.Parse("::ffff:10.0.0.5");
        Assert.True(svc.IsAllowed(mappedV6));
    }

    // ── 13. Invalid entries silently ignored ──────────────────────────────────

    [Fact]
    public void Invalid_entries_silently_ignored()
    {
        var svc = new AdminIpAllowlistService(["not-an-ip", "10.0.0.5"]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.0.0.5")));
        Assert.False(svc.IsAllowed(IPAddress.Parse("10.0.0.6")));
    }

    // ── 14. Whitespace trimmed ────────────────────────────────────────────────

    [Fact]
    public void Whitespace_trimmed()
    {
        var svc = new AdminIpAllowlistService(["  10.0.0.5  "]);
        Assert.True(svc.IsEnabled);
        Assert.True(svc.IsAllowed(IPAddress.Parse("10.0.0.5")));
    }

    // ── 15. Null IP blocked when enabled ─────────────────────────────────────

    [Fact]
    public void Null_ip_blocked_when_enabled()
    {
        var svc = new AdminIpAllowlistService(["10.0.0.1"]);
        Assert.True(svc.IsEnabled);
        Assert.False(svc.IsAllowed(null));
    }
}
