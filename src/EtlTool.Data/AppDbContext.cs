using EtlTool.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EtlTool.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ConnectionDefinition> Connections => Set<ConnectionDefinition>();
    public DbSet<EtlTask> EtlTasks => Set<EtlTask>();
    public DbSet<ColumnMapping> ColumnMappings => Set<ColumnMapping>();
    public DbSet<RunHistory> RunHistories => Set<RunHistory>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<EntityChangeHistory> EntityChangeHistories => Set<EntityChangeHistory>();
    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
    public DbSet<MaintenanceWindowEntity> MaintenanceWindows => Set<MaintenanceWindowEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ConnectionDefinition>(e =>
        {
            e.ToTable("Connections");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.EncryptedConnectionString).IsRequired();
            e.Property(x => x.ProviderType).HasConversion<int>();
            e.Property(x => x.LastCheckError).HasMaxLength(1000);
        });

        b.Entity<EtlTask>(e =>
        {
            e.ToTable("EtlTasks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.WriteMode).HasConversion<int>();
            e.Property(x => x.FilterMode).HasConversion<int>();
            e.Property(x => x.SchemaDriftPolicy).HasConversion<int>();
            e.Property(x => x.RowCountPolicy).HasConversion<int>();
            e.Property(x => x.CronExpression).HasMaxLength(100).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasMany(x => x.Mappings)
                .WithOne()
                .HasForeignKey(m => m.EtlTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ColumnMapping>(e =>
        {
            e.ToTable("ColumnMappings");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceColumn).HasMaxLength(200).IsRequired();
            e.Property(x => x.TargetColumn).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.EtlTaskId);
        });

        b.Entity<RunHistory>(e =>
        {
            e.ToTable("RunHistories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.TriggerType).HasConversion<int>();
            e.HasIndex(x => new { x.EtlTaskId, x.StartedAt });
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).HasConversion<int>();
            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.Severity).HasConversion<int>();
            e.Property(x => x.Message).IsRequired().HasMaxLength(500);
            e.Property(x => x.TargetType).HasMaxLength(50);
            e.Property(x => x.TargetName).HasMaxLength(200);
            e.Property(x => x.Actor).HasMaxLength(100);
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.Category, x.At });
            e.HasIndex(x => new { x.Severity, x.At });
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.PreviousHash).HasMaxLength(64);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasConversion<int>();
        });

        b.Entity<PasswordHistory>(e =>
        {
            e.ToTable("PasswordHistories");
            e.HasKey(x => x.Id);
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        b.Entity<MaintenanceWindowEntity>(e =>
        {
            e.ToTable("MaintenanceWindows");
            e.HasKey(x => x.Id);
            e.Property(x => x.Days).HasMaxLength(100).IsRequired();
            e.Property(x => x.From).HasMaxLength(5).IsRequired();
            e.Property(x => x.To).HasMaxLength(5).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
        });

        b.Entity<ApprovalRequest>(e =>
        {
            e.ToTable("ApprovalRequests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.TargetType).HasMaxLength(50).IsRequired();
            e.Property(x => x.TargetName).HasMaxLength(200);
            e.Property(x => x.SubmittedBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.DecidedBy).HasMaxLength(100);
            e.Property(x => x.SubmissionReason).HasMaxLength(500);
            e.Property(x => x.DecisionReason).HasMaxLength(500);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => new { x.TargetType, x.TargetId, x.Status });
        });

        b.Entity<EntityChangeHistory>(e =>
        {
            e.ToTable("EntityChangeHistories");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
            e.Property(x => x.EntityName).HasMaxLength(200);
            e.Property(x => x.ChangedBy).HasMaxLength(100);
            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.Summary).HasMaxLength(2000);
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.ChangedAt });
            e.HasIndex(x => x.ChangedAt);
        });
    }
}
