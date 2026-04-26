namespace EtlTool.Core.Models;

public class ConnectionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DbProviderType ProviderType { get; set; }

    public string EncryptedConnectionString { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // === 健康狀態（由 ConnectionHealthMonitor 背景服務更新）===
    public DateTime? LastCheckedAt { get; set; }
    public bool? LastCheckOk { get; set; }
    public string? LastCheckError { get; set; }
}
