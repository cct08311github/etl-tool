namespace EtlTool.Core.Models;

public enum AuditCategory
{
    Connection = 1,
    Task = 2,
    Run = 3,
    Scheduler = 4,
    System = 5,
    Auth = 6,
}

public enum AuditAction
{
    // 通用 CRUD
    Create = 1,
    Update = 2,
    Delete = 3,

    // 排程
    Schedule = 10,
    Unschedule = 11,
    TriggerNow = 12,

    // 執行
    RunStarted = 20,
    RunSucceeded = 21,
    RunFailed = 22,

    // 系統
    SystemStart = 40,
    SystemStop = 41,
    SchedulerInitialized = 42,
    TestConnection = 43,

    // 認證 (預留)
    Login = 50,
    Logout = 51,
    LoginFailed = 52,
}

public enum AuditSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.UtcNow;

    public AuditCategory Category { get; set; }
    public AuditAction Action { get; set; }
    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

    /// <summary>事件目標類別名稱，例如 "Connection"、"EtlTask"、"RunHistory"。</summary>
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>事件當下的人類可讀名稱（即使後續被刪除也保留意義）。</summary>
    public string? TargetName { get; set; }

    /// <summary>誰做的；目前無 auth 一律 "system"。</summary>
    public string? Actor { get; set; }

    /// <summary>單行訊息（UI 表格主欄）。</summary>
    public string Message { get; set; } = "";

    /// <summary>選用的結構化 payload (JSON 字串，例如錯誤明細)。</summary>
    public string? DetailsJson { get; set; }
}
