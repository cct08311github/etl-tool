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
}
