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

    /// <summary>Cookie 有效時間 (小時)。預設 8 小時。</summary>
    public int SessionHours { get; set; } = 8;
}
