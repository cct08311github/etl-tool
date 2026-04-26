using System.Net;
using System.Net.Sockets;

namespace EtlTool.App.Auth;

/// <summary>
/// 解析 Auth:AdminIpAllowlist 設定，並對任意 IP 判定是否允許。
///
/// 支援形式：
///   - 單一 IP："10.0.0.5", "192.168.1.10"
///   - CIDR：  "10.0.0.0/8", "192.168.1.0/24", "::1/128"
///   - 字面值："localhost", "loopback" → 127.0.0.1, ::1
///   - "*" 或空陣列 → 不啟用 allowlist（一律允許）
///
/// IPv4-mapped IPv6（"::ffff:10.0.0.5"）會在比對前 normalize 成 IPv4。
/// </summary>
public sealed class AdminIpAllowlistService
{
    private readonly List<IPAddress> _exactIps = new();
    private readonly List<(IPAddress network, int prefix)> _cidrs = new();
    public bool IsEnabled { get; }
    public IReadOnlyList<string> RawEntries { get; }

    public AdminIpAllowlistService(IConfiguration config)
        : this(config.GetSection("Auth:AdminIpAllowlist").Get<string[]>() ?? Array.Empty<string>())
    { }

    /// <summary>給單元測試用的 ctor。</summary>
    public AdminIpAllowlistService(IEnumerable<string> entries)
    {
        var list = entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        RawEntries = list;

        // "*" 表示停用 allowlist
        if (list.Count == 0 || list.Any(e => e == "*"))
        {
            IsEnabled = false;
            return;
        }

        IsEnabled = true;
        foreach (var entry in list)
        {
            if (TryParseEntry(entry, out var ips, out var cidrs))
            {
                _exactIps.AddRange(ips);
                _cidrs.AddRange(cidrs);
            }
        }
    }

    public bool IsAllowed(IPAddress? ip)
    {
        if (!IsEnabled) return true;
        if (ip is null) return false;

        var normalized = Normalize(ip);

        foreach (var allowed in _exactIps)
        {
            if (allowed.Equals(normalized)) return true;
        }
        foreach (var (network, prefix) in _cidrs)
        {
            if (IsInCidr(normalized, network, prefix)) return true;
        }
        return false;
    }

    public static bool TryParseEntry(string entry, out List<IPAddress> ips, out List<(IPAddress, int)> cidrs)
    {
        ips = new List<IPAddress>();
        cidrs = new List<(IPAddress, int)>();

        if (string.IsNullOrWhiteSpace(entry)) return false;
        entry = entry.Trim();

        if (entry.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            entry.Equals("loopback", StringComparison.OrdinalIgnoreCase))
        {
            ips.Add(IPAddress.Loopback);
            ips.Add(IPAddress.IPv6Loopback);
            return true;
        }

        var slashIdx = entry.IndexOf('/');
        if (slashIdx > 0)
        {
            var ipPart = entry[..slashIdx];
            var prefixPart = entry[(slashIdx + 1)..];
            if (!IPAddress.TryParse(ipPart, out var network)) return false;
            if (!int.TryParse(prefixPart, out var prefix)) return false;
            var max = network.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (prefix < 0 || prefix > max) return false;
            cidrs.Add((Normalize(network), prefix));
            return true;
        }

        if (IPAddress.TryParse(entry, out var single))
        {
            ips.Add(Normalize(single));
            return true;
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress ip)
    {
        // IPv4-mapped IPv6 ("::ffff:10.0.0.5") → IPv4
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4();
        return ip;
    }

    private static bool IsInCidr(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily) return false;
        if (prefix == 0) return true; // 0.0.0.0/0 一律允許

        var addrBytes = address.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (addrBytes.Length != netBytes.Length) return false;

        var fullBytes = prefix / 8;
        var remainBits = prefix % 8;

        for (int i = 0; i < fullBytes; i++)
            if (addrBytes[i] != netBytes[i]) return false;

        if (remainBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainBits));
        return (addrBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
    }
}
