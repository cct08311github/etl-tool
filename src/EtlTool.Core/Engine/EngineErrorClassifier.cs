namespace EtlTool.Core.Engine;

/// <summary>
/// 把 ETL 執行過程中拋出的例外歸類成幾個有意義的「錯誤類別」。銀行 ops 看到
/// run 失敗時最想第一眼知道：是不是該重試、要不要找 DBA、還是程式邏輯錯誤。
///
/// 分類規則完全純邏輯（檢查 message + 內層 exception chain），不依賴具體的
/// SqlClient / OracleClient assembly — 這樣 EtlTool.Core 可以零 DB 套件相依。
/// 受測試覆蓋；改規則時記得補對應 test case。
///
/// 三大類：
///   - <see cref="EngineErrorClass.Transient"/>: 網路抖動 / deadlock / lock timeout
///     → 通常重試會成功，retry policy 應該重試
///   - <see cref="EngineErrorClass.Permanent"/>: schema 不存在 / 權限錯誤 / PK 違反
///     → 重試只是浪費；ops 須介入
///   - <see cref="EngineErrorClass.Unknown"/>: 不確定（保守起見不重試）
///
/// Subkind 給更細緻的訊息（webhook routing / 追蹤統計用）。
/// </summary>
public static class EngineErrorClassifier
{
    public enum EngineErrorClass
    {
        Unknown = 0,
        Transient = 1,
        Permanent = 2,
    }

    public enum EngineErrorSubkind
    {
        Unknown = 0,

        // Transient
        TransientNetwork = 10,        // connection refused, RST, timeout connecting
        TransientDeadlock = 11,       // SQL Server 1205 / Oracle deadlock
        TransientLockTimeout = 12,    // SQL Server 1222
        TransientCommandTimeout = 13, // CommandTimeout 觸發

        // Permanent
        PermanentAuth = 20,           // login failed / wrong password
        PermanentSchemaMissing = 21,  // table / view / column 不存在
        PermanentSyntax = 22,         // 產生的 SQL 文法錯
        PermanentDataIntegrity = 23,  // PK / FK / unique violation
        PermanentPermissionDenied = 24, // 已登入但無此表權限
        PermanentTransformError = 25, // DynamicExpresso 編譯／執行錯誤
    }

    public sealed record Classification(EngineErrorClass Class, EngineErrorSubkind Subkind, string Reason);

    /// <summary>
    /// 對給定 Exception 沿 InnerException 找最深層、最有訊息量的那個 message 做匹配。
    /// </summary>
    public static Classification Classify(Exception? ex)
    {
        if (ex is null) return new Classification(EngineErrorClass.Unknown, EngineErrorSubkind.Unknown, "—");

        // 收集整個 inner chain 的 messages — 真正的底層錯誤通常在最內層
        var allMessages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (!string.IsNullOrEmpty(e.Message)) allMessages.Add(e.Message);
        }
        var combined = string.Join("\n", allMessages);
        var lower = combined.ToLowerInvariant();

        // 用 type name 也能輔助分類
        var typeName = ex.GetType().FullName ?? "";

        // ── Transient 類 ────────────────────────────────────────────────
        if (Contains(lower,
            "deadlock victim", "was deadlocked", "deadlocked on lock", "msg 1205",
            "ora-00060"))
        {
            return new(EngineErrorClass.Transient, EngineErrorSubkind.TransientDeadlock,
                "Deadlock — 與其他 transaction 互鎖，重試通常會通");
        }

        if (Contains(lower,
            "lock request time out", "lock timeout", "lock_timeout", "msg 1222"))
        {
            return new(EngineErrorClass.Transient, EngineErrorSubkind.TransientLockTimeout,
                "Lock timeout — 等鎖超過閾值，重試通常會通");
        }

        if (Contains(lower,
            "operation cancelled by user", "command was canceled", "command timeout expired",
            "execution timeout expired") ||
            (typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase) &&
             !lower.Contains("connection")))
        {
            return new(EngineErrorClass.Transient, EngineErrorSubkind.TransientCommandTimeout,
                "Command timeout — 查詢時間超過 CommandTimeout，可能是大批次或 source 慢");
        }

        if (Contains(lower,
            "connection refused", "no such host", "could not be opened", "network-related",
            "transport-level error", "timeout period elapsed", "broken pipe", "connection reset",
            "an existing connection was forcibly closed", "forcibly closed by the remote",
            "ora-12541", "ora-12545", "ora-12170", "ora-03113", "ora-03114"))
        {
            return new(EngineErrorClass.Transient, EngineErrorSubkind.TransientNetwork,
                "網路 / 連線抖動 — TCP 中斷、超時或 listener 不可達");
        }

        // ── Permanent 類 ────────────────────────────────────────────────
        if (Contains(lower,
            "login failed for user", "login failed", "msg 18456",
            "ora-01017", "invalid username/password",
            "kerberos authentication", "sspi handshake failed"))
        {
            return new(EngineErrorClass.Permanent, EngineErrorSubkind.PermanentAuth,
                "認證失敗 — 帳號／密碼／Kerberos token 錯誤；重試無用");
        }

        if (Contains(lower,
            "invalid object name", "msg 208",
            "ora-00942", "ora-00904",  // table or view does not exist / invalid identifier
            "ora-00903"))
        {
            return new(EngineErrorClass.Permanent, EngineErrorSubkind.PermanentSchemaMissing,
                "Schema/欄位不存在 — 上游表結構可能改了");
        }

        if (Contains(lower,
            "violation of primary key", "violation of unique key", "duplicate key",
            "msg 2627", "msg 2601",
            "ora-00001",  // unique constraint violated
            "violation of foreign key", "msg 547",
            "ora-02291", "ora-02292"))
        {
            return new(EngineErrorClass.Permanent, EngineErrorSubkind.PermanentDataIntegrity,
                "資料完整性違反 — PK / FK / unique 衝突，需要清資料或調整 IsKey");
        }

        if (Contains(lower,
            "permission denied", "the server principal is not able to access",
            "select permission was denied", "insert permission was denied",
            "update permission was denied", "delete permission was denied",
            "ora-00942"))  // ORA-00942 also fires on no-permission; 上面已處理為 schema missing 但訊息會帶 \"insufficient privileges\"
        {
            return new(EngineErrorClass.Permanent, EngineErrorSubkind.PermanentPermissionDenied,
                "DB 帳號權限不足 — 須在 source/target DB 補 GRANT");
        }

        if (Contains(lower,
            "incorrect syntax", "msg 102", "msg 137",
            "ora-00936", "ora-00933", "ora-00911"))
        {
            return new(EngineErrorClass.Permanent, EngineErrorSubkind.PermanentSyntax,
                "SQL 文法錯誤 — 多半是 raw filter SQL 寫錯");
        }

        if (typeName.Contains("ParserException", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("DynamicExpresso", StringComparison.OrdinalIgnoreCase) ||
            Contains(lower,
                "transform expression", "expression evaluation", "expression compile"))
        {
            return new(EngineErrorClass.Permanent, EngineErrorSubkind.PermanentTransformError,
                "Transform expression 錯誤 — 檢查 mapping 上的 C# 運算式");
        }

        return new(EngineErrorClass.Unknown, EngineErrorSubkind.Unknown,
            "未分類 — 訊息：" + (allMessages.LastOrDefault() ?? ex.Message));
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string ClassLabel(EngineErrorClass c) => c switch
    {
        EngineErrorClass.Transient => "暫時性錯誤（建議重試）",
        EngineErrorClass.Permanent => "永久性錯誤（重試無效）",
        _ => "未分類",
    };

    public static string SubkindLabel(EngineErrorSubkind s) => s switch
    {
        EngineErrorSubkind.TransientNetwork => "網路抖動",
        EngineErrorSubkind.TransientDeadlock => "Deadlock",
        EngineErrorSubkind.TransientLockTimeout => "Lock timeout",
        EngineErrorSubkind.TransientCommandTimeout => "Command timeout",
        EngineErrorSubkind.PermanentAuth => "認證失敗",
        EngineErrorSubkind.PermanentSchemaMissing => "Schema 不存在",
        EngineErrorSubkind.PermanentSyntax => "SQL 文法錯",
        EngineErrorSubkind.PermanentDataIntegrity => "資料完整性違反",
        EngineErrorSubkind.PermanentPermissionDenied => "權限不足",
        EngineErrorSubkind.PermanentTransformError => "Transform expression 錯誤",
        _ => "未分類",
    };
}
