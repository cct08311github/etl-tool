using System.Text;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

public class AuditExporterTests
{
    [Fact]
    public void CsvEscape_passthrough_for_simple_values()
    {
        Assert.Equal("hello", AuditExporter.CsvEscape("hello"));
        Assert.Equal("123", AuditExporter.CsvEscape("123"));
        Assert.Equal("", AuditExporter.CsvEscape(""));
    }

    [Fact]
    public void CsvEscape_quotes_when_comma_present()
    {
        Assert.Equal("\"a,b\"", AuditExporter.CsvEscape("a,b"));
    }

    [Fact]
    public void CsvEscape_quotes_when_newline_present()
    {
        Assert.Equal("\"line1\nline2\"", AuditExporter.CsvEscape("line1\nline2"));
    }

    [Fact]
    public void CsvEscape_escapes_internal_double_quotes()
    {
        // 內部 " 變 ""，且整體用 " 包起來
        Assert.Equal("\"he said \"\"hi\"\"\"", AuditExporter.CsvEscape("he said \"hi\""));
    }

    [Fact]
    public void FormatRow_outputs_in_header_order()
    {
        var e = new AuditEvent
        {
            Id = Guid.NewGuid(),
            At = new DateTime(2026, 4, 27, 10, 30, 0, DateTimeKind.Utc),
            Category = AuditCategory.Auth,
            Action = AuditAction.Login,
            Severity = AuditSeverity.Info,
            Actor = "alice",
            TargetType = "User",
            TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TargetName = "alice",
            Message = "logged in",
            Hash = "abc123",
            PreviousHash = null,
            DetailsJson = null,
        };
        var row = AuditExporter.FormatRow(e);
        var fields = row.Split(',');
        // 12 個欄位
        Assert.Equal(12, fields.Length);
        // At 為 ISO 8601 ("o") 格式，含 'T'
        Assert.Contains("T", fields[0]);
        Assert.Equal("Auth", fields[1]);
        Assert.Equal("Login", fields[2]);
        Assert.Equal("Info", fields[3]);
        Assert.Equal("alice", fields[4]);
        Assert.Equal("User", fields[5]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", fields[6]);
        Assert.Equal("alice", fields[7]);
        Assert.Equal("logged in", fields[8]);
        Assert.Equal("abc123", fields[9]);
        Assert.Equal("", fields[10]);  // PreviousHash null → ""
        Assert.Equal("", fields[11]);  // DetailsJson null → ""
    }

    [Fact]
    public async Task WriteCsvAsync_empty_input_writes_only_header()
    {
        var sw = new StringWriter();
        var cert = await AuditExporter.WriteCsvAsync(Array.Empty<AuditEvent>(), sw);
        Assert.Equal(0, cert.Total);
        Assert.Null(cert.FirstAt);
        Assert.Null(cert.LastAt);
        Assert.Null(cert.FirstHash);
        Assert.Null(cert.LastHash);

        var content = sw.ToString();
        Assert.StartsWith(AuditExporter.Header, content);
    }

    [Fact]
    public async Task WriteCsvAsync_records_first_and_last_hash()
    {
        var events = new[]
        {
            new AuditEvent { At = new DateTime(2026,4,1,1,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="m1", Hash="h1" },
            new AuditEvent { At = new DateTime(2026,4,1,2,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="m2", Hash="h2", PreviousHash="h1" },
            new AuditEvent { At = new DateTime(2026,4,1,3,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="m3", Hash="h3", PreviousHash="h2" },
        };

        var sw = new StringWriter();
        var cert = await AuditExporter.WriteCsvAsync(events, sw);
        Assert.Equal(3, cert.Total);
        Assert.Equal("h1", cert.FirstHash);
        Assert.Equal("h3", cert.LastHash);
        Assert.NotNull(cert.FirstAt);
        Assert.NotNull(cert.LastAt);
        Assert.True(cert.LastAt > cert.FirstAt);
        // SHA-256 是 64 hex chars
        Assert.Equal(64, cert.ExportBodySha256.Length);
        Assert.Matches("^[0-9A-F]+$", cert.ExportBodySha256);
    }

    [Fact]
    public async Task WriteCsvAsync_export_sha256_is_deterministic_across_runs()
    {
        var events = new[]
        {
            new AuditEvent { At = new DateTime(2026,4,1,1,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="hello", Hash="h1" },
        };

        var sw1 = new StringWriter();
        var cert1 = await AuditExporter.WriteCsvAsync(events, sw1);
        var sw2 = new StringWriter();
        var cert2 = await AuditExporter.WriteCsvAsync(events, sw2);

        Assert.Equal(cert1.ExportBodySha256, cert2.ExportBodySha256);
        Assert.Equal(sw1.ToString(), sw2.ToString());
    }

    [Fact]
    public async Task WriteCsvAsync_export_sha256_changes_when_content_changes()
    {
        var events1 = new[] { new AuditEvent { At = DateTime.UtcNow, Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="m1", Hash="h1" } };
        var events2 = new[] { new AuditEvent { At = DateTime.UtcNow, Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="m2", Hash="h1" } };

        var sw1 = new StringWriter();
        var cert1 = await AuditExporter.WriteCsvAsync(events1, sw1);
        var sw2 = new StringWriter();
        var cert2 = await AuditExporter.WriteCsvAsync(events2, sw2);

        Assert.NotEqual(cert1.ExportBodySha256, cert2.ExportBodySha256);
    }

    [Fact]
    public async Task WriteCertificateFooter_format()
    {
        var cert = new AuditExporter.ExportCertificate(
            Total: 5,
            FirstAt: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            LastAt: new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
            FirstHash: "h1",
            LastHash: "h5",
            ExportedAt: new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc),
            ExportBodySha256: "ABCDEF");

        var sw = new StringWriter();
        await AuditExporter.WriteCertificateFooterAsync(cert, sw);
        var line = sw.ToString().TrimEnd('\r', '\n');

        Assert.StartsWith("# CERTIFICATE: ", line);
        Assert.Contains("Total=5", line);
        Assert.Contains("FirstHash=h1", line);
        Assert.Contains("LastHash=h5", line);
        Assert.Contains("ExportBodySha256=ABCDEF", line);
        Assert.Contains("FirstAt=2026-04-01", line);
    }

    [Fact]
    public async Task FullRoundtrip_csv_can_be_split_by_lines()
    {
        var events = new[]
        {
            new AuditEvent { At = new DateTime(2026,4,1,1,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="line one", Hash="h1" },
            new AuditEvent { At = new DateTime(2026,4,1,2,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="has,comma", Hash="h2" },
            new AuditEvent { At = new DateTime(2026,4,1,3,0,0,DateTimeKind.Utc), Category=AuditCategory.System, Action=AuditAction.SystemStart, Severity=AuditSeverity.Info, Message="has\"quote", Hash="h3" },
        };

        var sw = new StringWriter();
        var cert = await AuditExporter.WriteCsvAsync(events, sw);
        await AuditExporter.WriteCertificateFooterAsync(cert, sw);

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 rows + 1 cert = 5 行（注意 has,comma / has"quote 都是被引號包起來，一行）
        Assert.Equal(5, lines.Length);
        Assert.StartsWith(AuditExporter.Header, lines[0]);
        Assert.StartsWith("# CERTIFICATE:", lines[4]);
    }

    [Fact]
    public async Task WriteCsvAsync_orders_first_and_last_by_input_order_not_event_at()
    {
        // Exporter 按 input 順序記 first/last（呼叫者負責先 OrderBy）
        var events = new[]
        {
            new AuditEvent { At = new DateTime(2026,4,1,3,0,0,DateTimeKind.Utc), Hash="h3" },
            new AuditEvent { At = new DateTime(2026,4,1,1,0,0,DateTimeKind.Utc), Hash="h1" },
            new AuditEvent { At = new DateTime(2026,4,1,2,0,0,DateTimeKind.Utc), Hash="h2" },
        };
        var sw = new StringWriter();
        var cert = await AuditExporter.WriteCsvAsync(events, sw);
        Assert.Equal("h3", cert.FirstHash);
        Assert.Equal("h2", cert.LastHash);
    }
}
