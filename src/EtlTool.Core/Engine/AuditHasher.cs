using System.Security.Cryptography;
using System.Text;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// AuditEvent 的可重現 SHA-256 hash 計算。
///
/// 編碼規則（| 分隔，UTF-8）：
///   prev|At(ISO8601 round-trip 強制 UTC)|Category(int)|Action(int)|Severity(int)
///       |TargetType|TargetId|TargetName|Actor|Message|DetailsJson
///
/// 注意：
///   - 不含 Hash 自己（chicken-and-egg）；不含 Id（測試 / 重放方便）
///   - At 用 SpecifyKind(Utc) + "O" round-trip 格式 — 強制 Z 後綴，避免
///     SQLite TEXT 來回轉換時 DateTimeKind 變 Unspecified 造成 hash 不一致
///   - 所有 nullable 字串：null → 空字串
///   - 任何欄位變動 → hash 改變 → 鏈斷裂可被偵測
///
/// 歷史 bug 註記（2026-04-27 修）：
///   原版用 e.At.ToString("O")。EF Core SQLite provider 把 DateTime 存成 TEXT，
///   讀回時 Kind = Unspecified。寫入時 Kind=Utc → "...Z"；讀回 Kind=Unspecified → "..."（無Z）。
///   兩個字串不同 → 重算 hash 與儲存的 hash 不符 → AuditChainVerifier 誤報「被竄改」。
///   修法：強制 SpecifyKind(Utc) 後再 ToString("O")。對既存資料 hash 也會匹配，
///   因為原本就是 UTC 時刻；只是把 Kind 標籤補回去再格式化。
/// </summary>
public static class AuditHasher
{
    public static string ComputeHash(AuditEvent e, string? previousHash)
    {
        var sb = new StringBuilder(512);
        sb.Append(previousHash ?? "");
        // 強制以 UTC 標籤輸出 — SQLite round-trip 會丟失 DateTimeKind
        var atUtc = DateTime.SpecifyKind(e.At, DateTimeKind.Utc);
        sb.Append('|').Append(atUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
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
