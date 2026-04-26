using System.Text.RegularExpressions;

namespace EtlTool.Core.Engine;

/// <summary>
/// 偵測連線字串中常見的弱密碼。Banks 應禁止把 production 連線指向 dev 帳密、
/// 或使用容易被字典攻擊的密碼。
///
/// 設計：
///   - 不看連線字串其他欄位（host / db / user）— 只解析出 password 比對
///   - 比對方式：lowercase 完全相符 OR 以特定 weak prefix 開頭
///   - 同時比對「常見 dev/sample 帳密」（例：sa/Dev_Password1!、system/oracle）
///   - 回傳結構化結果，呼叫端決定 block / warn / log
/// </summary>
public static class WeakCredentialDetector
{
    /// <summary>常見 weak passwords（lowercase 比對）。</summary>
    private static readonly HashSet<string> WeakPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic
        "password", "pass", "pwd", "secret",
        "admin", "administrator", "root",
        "12345", "123456", "1234567", "12345678", "123456789", "1234567890",
        "qwerty", "qwertyuiop", "abc123", "letmein", "welcome", "test",
        // 開發環境常見 sample
        "oracle", "manager", "tiger",                // Oracle defaults
        "dev_password1!", "p@ssw0rd", "p@ssw0rd1",   // mssql dev
        "changeme", "default",
        "etl", "etltool",
        // 全相同字元
        "aaaaaa", "111111", "000000",
    };

    /// <summary>常見 dev / sample 帳密 pair（user → password lowercase）。</summary>
    private static readonly Dictionary<string, string> DevAccountPairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sa"] = "dev_password1!",
        ["system"] = "oracle",
        ["sys"] = "oracle",
        ["scott"] = "tiger",
        ["hr"] = "hr",
        ["admin"] = "admin",
        ["root"] = "root",
    };

    public sealed record DetectionResult(
        WeakCredentialKind Kind,
        string? Detail);

    public static DetectionResult Inspect(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new DetectionResult(WeakCredentialKind.None, null);

        var (user, password) = ExtractUserPassword(connectionString);

        // 1) 完全沒有密碼（Integrated Security 不算 weak — 那是 OS 認證）
        if (string.IsNullOrEmpty(password))
        {
            // 看是否是 Integrated Security
            if (HasIntegratedSecurity(connectionString))
                return new DetectionResult(WeakCredentialKind.None, null);
            return new DetectionResult(WeakCredentialKind.EmptyPassword,
                "連線字串未包含密碼且非 Integrated Security 模式");
        }

        // 2) **先檢查** dev/sample 帳密 pair — 比起「密碼太短」或「常見弱密碼」，
        //    給「這是 sa/Dev_Password1! sample creds」更具體更可操作的訊息。
        //    不然 oracle/tiger/admin 這類短密碼會被先攔到 TooShort 而看不出真正風險所在。
        if (user is not null && DevAccountPairs.TryGetValue(user, out var expectedDev)
            && string.Equals(password, expectedDev, StringComparison.OrdinalIgnoreCase))
        {
            return new DetectionResult(WeakCredentialKind.KnownDevPair,
                $"使用了開發 / 範例環境的預設帳密（user={user}），切勿用於 production");
        }

        // 3) user == password
        if (user is not null && string.Equals(user, password, StringComparison.OrdinalIgnoreCase))
        {
            return new DetectionResult(WeakCredentialKind.UserEqualsPassword,
                "使用者名稱與密碼相同");
        }

        // 4) 太短
        if (password.Length < 8)
        {
            return new DetectionResult(WeakCredentialKind.TooShort,
                $"密碼長度僅 {password.Length} 字元（建議 ≥ 8）");
        }

        // 5) 是常見 weak password
        if (WeakPasswords.Contains(password))
        {
            return new DetectionResult(WeakCredentialKind.CommonWeakPassword,
                $"密碼是常見弱密碼之一（{Mask(password)}），請改為強密碼");
        }

        // 6) 純數字（弱密碼模式）
        if (Regex.IsMatch(password, @"^\d+$"))
        {
            return new DetectionResult(WeakCredentialKind.AllDigits,
                "密碼為純數字（缺少大小寫字母與符號）");
        }

        return new DetectionResult(WeakCredentialKind.None, null);
    }

    /// <summary>給 UI 用：是否要 block save（強烈反對），或只是 warn（讓使用者按確認後通過）。</summary>
    public static bool IsBlocking(WeakCredentialKind kind) => kind switch
    {
        WeakCredentialKind.None => false,
        WeakCredentialKind.TooShort => false,            // warn only — 內部可能有歷史短密碼
        _ => true,                                        // 其餘（empty / common / dev pair / user==pwd / digits）→ block
    };

    public static (string? user, string? password) ExtractUserPassword(string cs)
    {
        // 解析 key=value pairs（;分隔），對 user / password key 做提取
        // SqlClient + Oracle 都支援多種別名：User Id, UID, User, Username; Password, PWD
        string? user = null;
        string? pass = null;

        foreach (var pair in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key = pair[..idx].Trim();
            var val = pair[(idx + 1)..].Trim();
            // 去掉雙引號（若有）
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') val = val[1..^1];
            if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'') val = val[1..^1];

            if (IsUserKey(key)) user ??= val;
            else if (IsPasswordKey(key)) pass ??= val;
        }
        return (user, pass);
    }

    private static bool IsUserKey(string key) =>
        key.Equals("user id", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("uid", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("user", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("username", StringComparison.OrdinalIgnoreCase);

    private static bool IsPasswordKey(string key) =>
        key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("pwd", StringComparison.OrdinalIgnoreCase);

    private static bool HasIntegratedSecurity(string cs)
    {
        foreach (var pair in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key = pair[..idx].Trim();
            var val = pair[(idx + 1)..].Trim();
            if ((key.Equals("integrated security", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("trusted_connection", StringComparison.OrdinalIgnoreCase))
                && (val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("sspi", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string Mask(string s)
    {
        if (s.Length <= 2) return new string('*', s.Length);
        return s[0] + new string('*', s.Length - 2) + s[^1];
    }
}

public enum WeakCredentialKind
{
    None = 0,
    EmptyPassword = 1,
    TooShort = 2,
    CommonWeakPassword = 3,
    KnownDevPair = 4,
    UserEqualsPassword = 5,
    AllDigits = 6,
}
