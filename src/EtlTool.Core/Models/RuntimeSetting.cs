namespace EtlTool.Core.Models;

/// <summary>
/// 簡單 key-value 形式的執行期可調設定。Admin 在 /system 頁可即時修改，
/// 不需要重啟服務。每次變更都寫入 audit + EntityChangeHistory（透過 repo 統一處理）。
///
/// 不所有設定都用這張表 — 只有「維運可調項」（webhook URL / 格式 / rate limit 之類）。
/// 「基礎建設 / 安全相關」（admin IP allowlist、HTTPS 強制、保留政策）仍走 appsettings.json
/// + 重啟，避免 UI 被攻破時整個系統失守。
///
/// 讀取優先順序（在 RuntimeSettingsService 統一處理）：
///   1. DB Settings 表（優先）
///   2. appsettings.json (回退)
///   3. 程式碼預設值
/// </summary>
public class RuntimeSetting
{
    /// <summary>設定鍵 — 與 IConfiguration key 同樣的點分形式（"Webhooks:OnFailure"）。</summary>
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
