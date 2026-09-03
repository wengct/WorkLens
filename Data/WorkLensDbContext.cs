using Microsoft.EntityFrameworkCore;
using WorkLens.Domain;

namespace WorkLens.Data;

public sealed class WorkLensDbContext(DbContextOptions<WorkLensDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<WorkEntry> WorkEntries => Set<WorkEntry>();
    public DbSet<ActivitySource> ActivitySources => Set<ActivitySource>();
    public DbSet<SourceEvidence> SourceEvidence => Set<SourceEvidence>();
    public DbSet<CommitLineage> CommitLineages => Set<CommitLineage>();
    public DbSet<RebaseSession> RebaseSessions => Set<RebaseSession>();
    public DbSet<ReportDocument> Reports => Set<ReportDocument>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<AiProviderConfiguration> AiProviders => Set<AiProviderConfiguration>();
    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActivitySource>().Property(x => x.SourceType).HasConversion<string>();
        modelBuilder.Entity<ActivitySource>().Property(x => x.HealthStatus).HasConversion<string>();
        modelBuilder.Entity<SourceEvidence>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<SourceEvidence>().Property(x => x.ReachabilityStatus).HasConversion<string>();
        modelBuilder.Entity<CommitLineage>().Property(x => x.Relation).HasConversion<string>();
        modelBuilder.Entity<ReportDocument>().Property(x => x.Kind).HasConversion<string>();

        modelBuilder.Entity<ActivitySource>()
            .HasIndex(x => new { x.DisplayName, x.IsArchived });

        modelBuilder.Entity<WorkEntry>()
            .HasIndex(x => x.WorkDate);

        modelBuilder.Entity<SourceEvidence>()
            .HasIndex(x => new { x.RepositoryKey, x.ExternalKey })
            .IsUnique();

        modelBuilder.Entity<SourceEvidence>()
            .HasIndex(x => new { x.RepositoryKey, x.CommitHash });

        modelBuilder.Entity<CommitLineage>()
            .HasIndex(x => new { x.RepositoryKey, x.OldCommitHash, x.NewCommitHash })
            .IsUnique();

        modelBuilder.Entity<RebaseSession>()
            .HasIndex(x => new { x.RepositoryKey, x.ExternalKey })
            .IsUnique();

        modelBuilder.Entity<ReportDocument>()
            .HasIndex(x => new { x.Kind, x.PeriodKey })
            .IsUnique();

        modelBuilder.Entity<AiProviderConfiguration>()
            .HasKey(x => x.Id);
    }
}
