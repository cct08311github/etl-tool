namespace EtlTool.Core.Models;

/// <summary>
/// DB-backed 維護視窗（admin 透過 UI 動態管理）。
///
/// 與 EtlTool.Core.Scheduling.MaintenanceWindow（appsettings static 來源）不同：
/// 此實體存資料庫、可在 admin 頁即時新增/編輯/停用，不需重啟。
/// 兩個來源在 IMaintenanceWindowProvider 內合併為單一視圖。
///
/// 同樣支援跨午夜 (From > To)。
/// </summary>
public class MaintenanceWindowEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"All" 或 "Mon,Tue,Wed,..." 逗號分隔；存原始字串方便 UI 編輯。</summary>
    public string Days { get; set; } = "All";

    /// <summary>HH:mm 24h。</summary>
    public string From { get; set; } = "00:00";

    /// <summary>HH:mm 24h；From > To 表示跨午夜。</summary>
    public string To { get; set; } = "00:00";

    public string? Reason { get; set; }

    /// <summary>未啟用 = 不參與 maintenance 計算。Admin 可暫停 / 啟用個別視窗。</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
