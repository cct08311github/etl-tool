namespace EtlTool.App.Auth;

/// <summary>
/// 從 appsettings.json 的 "Auth" 段讀取的單一帳戶設定。
/// 三種狀態（按優先序）：
///   1) PasswordHash 設了 → 用 BCrypt verify (推薦於正式環境)
///   2) Password 設了 → 純文字比對 (內部測試用，會在 log 警告)
///   3) 全空 → 使用編譯期預設密碼，並在 log 警告 (僅供初次啟動)
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// Session 不活動逾時（分鐘）。預設 30，銀行內網建議 15-30。
    /// 同時控制 Cookie ExpireTimeSpan + SlidingExpiration，所以「N 分鐘無操作會自動登出」。
    /// 若需要長時段（例如長執行 ETL 監控），可調至最高 480（8 小時）。
    /// </summary>
    public int SessionTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// 已棄用：請改用 SessionTimeoutMinutes。仍讀取以維持向後相容；
    /// 若同時設定，SessionTimeoutMinutes 優先。
    /// </summary>
    public int SessionHours { get; set; } = 0;

    /// <summary>實際採用的 timeout（分鐘）。給 cookie 設定讀。</summary>
    public int ResolveTimeoutMinutes()
    {
        if (SessionTimeoutMinutes > 0) return Math.Min(SessionTimeoutMinutes, 480);
        if (SessionHours > 0) return Math.Min(SessionHours * 60, 480);
        return 30;
    }

    /// <summary>
    /// 密碼最大年齡（天）。超過後強制變更（user 下次登入會被導向 ChangePassword）。
    /// 0 或未設 = 不啟用（不強制 rotation）。
    /// 銀行常見：90 天 (PCI-DSS) 或 180 天。
    /// </summary>
    public int MaxPasswordAgeDays { get; set; } = 0;

    /// <summary>到期前 N 天開始顯示警告 banner（給 user 提前換）。預設 14 天。</summary>
    public int PasswordExpiryWarnDays { get; set; } = 14;
}
