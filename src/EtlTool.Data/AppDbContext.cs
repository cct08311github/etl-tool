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
            e.Property(x => x.CronExpression).HasMaxLength(100).IsRequired();
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
    }
}
