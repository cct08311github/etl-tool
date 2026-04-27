using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 給 ConnectionEdit 存檔時用的「連線字串強度檢查」。純查表 + 字串比對，
/// 不打 DB，不 throw — 回傳 advisory 列表給 UI 顯示。
///
/// 銀行常見 misconfig：
///   - 密碼空 / 弱（4 字元 / 純數字）
///   - 開發階段把 TrustServerCertificate=true 留著上 prod
///   - Encrypt=false 跨網路連線（明文流量）
///   - SQL Server: 預期 SQL auth 但寫成 Integrated Security=true（NT 認證）
///   - Oracle: SYS / SYSTEM 帳號跑業務（過大權限）
///
/// 「Strict 模式」（給 prod ConnectionEdit）：把 Suggestion 也視為 Warning。
/// 預設只回 Warning。
/// </summary>
public static class ConnectionStringInspector
{
    public enum AdvisorySeverity
    {
        Info = 0,
        Suggestion = 1,
        Warning = 2,
    }

    public sealed record Advisory(AdvisorySeverity Severity, string Code, string Message);

    public static IReadOnlyList<Advisory> Inspect(DbProviderType provider, string connectionString)
    {
        var result = new List<Advisory>();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            result.Add(new Advisory(AdvisorySeverity.Warning, "EMPTY", "連線字串為空"));
            return result;
        }

        // 簡單的 key=value 解析（不處理 quote 內的 ;）。對 90% case 夠用；
        // 若要精準，呼叫端可改用 SqlConnectionStringBuilder / OracleConnectionStringBuilder。
        var kvs = ParseKvs(connectionString);
        var (passwordKey, passwordValue) = ExtractPassword(kvs);

        // Provider-specific
        switch (provider)
        {
            case DbProviderType.SqlServer:
                InspectSqlServer(kvs, result);
                break;
            case DbProviderType.Oracle:
                InspectOracle(kvs, result);
                break;
        }

        // Generic password-strength（兩家都檢）
        if (passwordValue is not null)
        {
            CheckPasswordStrength(passwordValue, passwordKey ?? "Password", result);
        }
        else if (!HasIntegratedSecurity(kvs))
        {
            // 沒寫密碼也沒寫 integrated security → 多半是漏帶
            result.Add(new Advisory(AdvisorySeverity.Warning, "NO_AUTH",
                "連線字串既無密碼也未設 Integrated Security/Trusted_Connection — 連線時會失敗"));
        }

        return result;
    }

    private static void InspectSqlServer(IDictionary<string, string> kvs, List<Advisory> result)
    {
        if (TryGetBool(kvs, "TrustServerCertificate", out var trust) && trust)
            result.Add(new Advisory(AdvisorySeverity.Warning, "TRUST_SERVER_CERT",
                "TrustServerCertificate=true — 跳過 SSL 憑證驗證；prod 應移除此選項"));

        // Encrypt：MSSQL Driver 18 起預設 = true；舊樣板 / migration 來的可能還寫 false
        if (TryGetString(kvs, "Encrypt", out var enc) &&
            (enc.Equals("false", StringComparison.OrdinalIgnoreCase) ||
             enc.Equals("0", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new Advisory(AdvisorySeverity.Warning, "ENCRYPT_FALSE",
                "Encrypt=false — 連線資料明文傳輸；prod 強烈建議改 true 或移除（預設 true）"));
        }

        if (HasIntegratedSecurity(kvs))
        {
            result.Add(new Advisory(AdvisorySeverity.Suggestion, "INTEGRATED_SECURITY",
                "使用 Integrated Security/Trusted_Connection — 確認 Windows 服務帳號或執行身分有目標 DB 的 mapped login"));
        }

        // 太短的 Connection Timeout 容易在網路抖時誤判失敗（常見抄壞舊 conn string）
        if (TryGetInt(kvs, "Connection Timeout", out var ct) && ct > 0 && ct < 5)
            result.Add(new Advisory(AdvisorySeverity.Suggestion, "SHORT_CONN_TIMEOUT",
                $"Connection Timeout={ct} 太短，網路抖時會誤判失敗。建議 15+。"));
    }

    private static void InspectOracle(IDictionary<string, string> kvs, List<Advisory> result)
    {
        if (TryGetString(kvs, "User Id", out var userId) || TryGetString(kvs, "User ID", out userId))
        {
            var lower = userId.Trim().Trim('"').ToLowerInvariant();
            if (lower is "sys" or "system")
            {
                result.Add(new Advisory(AdvisorySeverity.Warning, "PRIV_USER",
                    $"User={userId} — 使用 SYS / SYSTEM 帳號跑業務 ETL 是過度授權；建議建立專用帳號並 GRANT 必要權限"));
            }
        }
    }

    private static void CheckPasswordStrength(string password, string keyName, List<Advisory> result)
    {
        var len = password.Length;

        if (len == 0)
        {
            result.Add(new Advisory(AdvisorySeverity.Warning, "EMPTY_PASSWORD",
                $"{keyName} 為空字串"));
            return;
        }

        if (len < 8)
        {
            result.Add(new Advisory(AdvisorySeverity.Warning, "SHORT_PASSWORD",
                $"密碼僅 {len} 字元（< 8）；銀行內控標準通常要求 12+"));
            return;
        }

        // 純數字 / 純字母 → 弱
        bool allDigits = password.All(char.IsDigit);
        bool allLetters = password.All(char.IsLetter);
        if (allDigits || allLetters)
        {
            result.Add(new Advisory(AdvisorySeverity.Suggestion, "MONO_CHARSET",
                $"密碼{(allDigits ? "純數字" : "純字母")} — 建議混合字母／數字／符號"));
        }

        // 常見弱密碼 — 不窮舉，只列幾個明顯
        var common = new[] { "password", "passw0rd", "12345678", "qwerty1234", "sa123456", "admin1234" };
        if (common.Contains(password.ToLowerInvariant()))
        {
            result.Add(new Advisory(AdvisorySeverity.Warning, "COMMON_PASSWORD",
                "密碼疑為常見弱密碼（如 password / 12345678 / sa123456）— 強烈建議更換"));
        }
    }

    // ── helpers ────────────────────────────────────────────────────────
    private static IDictionary<string, string> ParseKvs(string cs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var k = pair[..idx].Trim();
            var v = pair[(idx + 1)..].Trim();
            // 移除可能的引號包覆
            if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
                v = v[1..^1];
            if (!d.ContainsKey(k)) d[k] = v;
        }
        return d;
    }

    private static (string? Key, string? Value) ExtractPassword(IDictionary<string, string> kvs)
    {
        // SQL Server: Password / Pwd
        // Oracle: Password
        foreach (var k in new[] { "Password", "Pwd" })
        {
            if (kvs.TryGetValue(k, out var v))
                return (k, v);
        }
        return (null, null);
    }

    private static bool HasIntegratedSecurity(IDictionary<string, string> kvs)
    {
        if (TryGetBool(kvs, "Integrated Security", out var b1) && b1) return true;
        if (kvs.TryGetValue("Integrated Security", out var s1) &&
            s1.Equals("SSPI", StringComparison.OrdinalIgnoreCase)) return true;
        if (TryGetBool(kvs, "Trusted_Connection", out var b2) && b2) return true;
        return false;
    }

    private static bool TryGetString(IDictionary<string, string> kvs, string key, out string value)
    {
        if (kvs.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) { value = v; return true; }
        value = "";
        return false;
    }

    private static bool TryGetBool(IDictionary<string, string> kvs, string key, out bool value)
    {
        value = false;
        if (!kvs.TryGetValue(key, out var raw)) return false;
        return bool.TryParse(raw, out value)
            || (raw == "1" && (value = true) == true)
            || (raw.Equals("yes", StringComparison.OrdinalIgnoreCase) && (value = true) == true);
    }

    private static bool TryGetInt(IDictionary<string, string> kvs, string key, out int value)
    {
        value = 0;
        return kvs.TryGetValue(key, out var raw) && int.TryParse(raw, out value);
    }

    public static string SeverityLabel(AdvisorySeverity s) => s switch
    {
        AdvisorySeverity.Warning => "警告",
        AdvisorySeverity.Suggestion => "建議",
        AdvisorySeverity.Info => "資訊",
        _ => s.ToString(),
    };
}
