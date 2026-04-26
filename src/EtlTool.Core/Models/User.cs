namespace EtlTool.Core.Models;

public enum UserRole
{
    /// <summary>所有功能；可管理使用者。</summary>
    Admin = 1,

    /// <summary>建立 / 編輯 / 觸發 ETL；不可管理使用者；不可刪除任務 / 連線（v2 才實施）。</summary>
    Operator = 2,

    /// <summary>唯讀；可查看任務、執行歷史、log。</summary>
    Viewer = 3,
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    /// <summary>BCrypt 雜湊；不存純文字。</summary>
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Viewer;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// 若為 true，下次登入會強制導向 /Account/ChangePassword，
    /// 直到完成密碼變更才能正常使用其他頁面。
    ///
    /// 銀行需求：新建帳號 / admin 重設密碼 / 強制 rotation 場景皆設為 true，
    /// 確保「temp password 不可長期使用」原則。
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>密碼最後一次成功變更的時間（UTC）。供「N 天到期」策略使用。</summary>
    public DateTime LastPasswordChangedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 保留每位使用者最近 N 個歷史密碼 hash，避免重複使用。
/// 銀行常見 N=3 或 N=5；本系統預設 5（可由設定調整）。
/// </summary>
public class PasswordHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
