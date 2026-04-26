using System.Text;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把 AuditEvent 序列匯出成 CSV，並在尾端附上「審計鏈摘要」cert footer，
/// 供外部稽核 / 監理單位驗證匯出檔案內容未經竄改。
///
/// 設計：
///   - 純函式，吃 IEnumerable&lt;AuditEvent&gt; + TextWriter，不耦合 EF / DI
///   - 純 .NET，不依賴 CsvHelper（避免 NuGet 相依）
///   - Footer cert 包含：
///       Total: N
///       FirstAt / LastAt
///       FirstHash / LastHash
///       ExportedAt (UTC ISO8601)
///       ExportSha256 — 對 CSV body（不含 footer）的 SHA-256
///   - 驗證者可以重算 ExportSha256 + 比對 first/last hash 與 live DB 的鏈，
///     確認檔案內容＝匯出當下的 DB 切片，且鏈完整。
/// </summary>
public static class AuditExporter
{
    public const string Header =
        "At,Category,Action,Severity,Actor,TargetType,TargetId,TargetName,Message,Hash,PreviousHash,DetailsJson";

    /// <summary>把 events 寫成 CSV 給 writer。回傳 cert（給呼叫端決定要不要把 footer 寫進同一檔或另開檔）。</summary>
    public static async Task<ExportCertificate> WriteCsvAsync(
        IEnumerable<AuditEvent> events, TextWriter writer, CancellationToken ct = default)
    {
        await writer.WriteLineAsync(Header);

        long total = 0;
        DateTime? firstAt = null;
        DateTime? lastAt = null;
        string? firstHash = null;
        string? lastHash = null;

        // 邊寫邊算 SHA-256（body only，不含 header / footer）
        using var sha = System.Security.Cryptography.SHA256.Create();

        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            total++;

            var line = FormatRow(e);
            await writer.WriteLineAsync(line);

            // 一行一行 hash
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);

            if (firstAt is null) { firstAt = e.At; firstHash = e.Hash; }
            lastAt = e.At;
            lastHash = e.Hash;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var bodyHash = Convert.ToHexString(sha.Hash!);

        var cert = new ExportCertificate(
            Total: total,
            FirstAt: firstAt,
            LastAt: lastAt,
            FirstHash: firstHash,
            LastHash: lastHash,
            ExportedAt: DateTime.UtcNow,
            ExportBodySha256: bodyHash);

        return cert;
    }

    /// <summary>把 cert 以「# CERTIFICATE: key=value; key=value ...」格式 append 到 writer。</summary>
    public static Task WriteCertificateFooterAsync(ExportCertificate cert, TextWriter writer)
    {
        var line =
            $"# CERTIFICATE: " +
            $"Total={cert.Total}; " +
            $"FirstAt={cert.FirstAt?.ToString("o") ?? "-"}; " +
            $"LastAt={cert.LastAt?.ToString("o") ?? "-"}; " +
            $"FirstHash={cert.FirstHash ?? "-"}; " +
            $"LastHash={cert.LastHash ?? "-"}; " +
            $"ExportedAt={cert.ExportedAt:o}; " +
            $"ExportBodySha256={cert.ExportBodySha256}";
        return writer.WriteLineAsync(line);
    }

    /// <summary>單一筆 audit event → CSV 行（已 escape）。</summary>
    public static string FormatRow(AuditEvent e)
    {
        // 按 Header 欄位順序
        return string.Join(',',
            CsvEscape(e.At.ToString("o")),
            CsvEscape(e.Category.ToString()),
            CsvEscape(e.Action.ToString()),
            CsvEscape(e.Severity.ToString()),
            CsvEscape(e.Actor ?? ""),
            CsvEscape(e.TargetType ?? ""),
            CsvEscape(e.TargetId?.ToString() ?? ""),
            CsvEscape(e.TargetName ?? ""),
            CsvEscape(e.Message),
            CsvEscape(e.Hash ?? ""),
            CsvEscape(e.PreviousHash ?? ""),
            CsvEscape(e.DetailsJson ?? ""));
    }

    /// <summary>RFC 4180 CSV escape：含 ',' '"' '\n' '\r' 時用 "" 包起來，內部 " 變 ""。</summary>
    public static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var needsQuotes = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuotes) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public sealed record ExportCertificate(
        long Total,
        DateTime? FirstAt,
        DateTime? LastAt,
        string? FirstHash,
        string? LastHash,
        DateTime ExportedAt,
        string ExportBodySha256);
}
