using System.Security.Cryptography;
using System.Text;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// AuditEvent 的可重現 SHA-256 hash 計算。
///
/// 編碼規則（| 分隔，UTF-8）：
///   prev|At(ISO8601 round-trip)|Category(int)|Action(int)|Severity(int)
///       |TargetType|TargetId|TargetName|Actor|Message|DetailsJson
///
/// 注意：
///   - 不含 Hash 自己（chicken-and-egg）；不含 Id（測試 / 重放方便）
///   - At 用 "O" round-trip 格式，包含時區與毫秒，確保跨 OS 一致
///   - 所有 nullable 字串：null → 空字串
///   - 任何欄位變動 → hash 改變 → 鏈斷裂可被偵測
/// </summary>
public static class AuditHasher
{
    public static string ComputeHash(AuditEvent e, string? previousHash)
    {
        var sb = new StringBuilder(512);
        sb.Append(previousHash ?? "");
        sb.Append('|').Append(e.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('|').Append((int)e.Category);
        sb.Append('|').Append((int)e.Action);
        sb.Append('|').Append((int)e.Severity);
        sb.Append('|').Append(e.TargetType ?? "");
        sb.Append('|').Append(e.TargetId?.ToString() ?? "");
        sb.Append('|').Append(e.TargetName ?? "");
        sb.Append('|').Append(e.Actor ?? "");
        sb.Append('|').Append(e.Message ?? "");
        sb.Append('|').Append(e.DetailsJson ?? "");

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash); // 64 字大寫 hex
    }
}
