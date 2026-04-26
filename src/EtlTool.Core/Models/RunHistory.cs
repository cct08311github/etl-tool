namespace EtlTool.Core.Models;

public class RunHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EtlTaskId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;
    public TriggerType TriggerType { get; set; } = TriggerType.Scheduled;

    public long RowsRead { get; set; }
    public long RowsWritten { get; set; }

    public string? GeneratedReadSql { get; set; }
    public string? GeneratedWriteSql { get; set; }
    public string? SamplePayloadJson { get; set; }

    public string? ErrorMessage { get; set; }
}
