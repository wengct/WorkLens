using Microsoft.EntityFrameworkCore;
using WorkLens.Domain;

namespace WorkLens.Data;

public sealed class WorkLensDbContext(DbContextOptions<WorkLensDbContext> options) : DbContext(options)
{
    public DbSet<WorkDraft> WorkDrafts => Set<WorkDraft>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<WorkEntry> WorkEntries => Set<WorkEntry>();
    public DbSet<ActivitySource> ActivitySources => Set<ActivitySource>();
    public DbSet<SourceEvidence> SourceEvidence => Set<SourceEvidence>();
    public DbSet<CommitLineage> CommitLineages => Set<CommitLineage>();
    public DbSet<RebaseSession> RebaseSessions => Set<RebaseSession>();
    public DbSet<ReportDocument> Reports => Set<ReportDocument>();
    public DbSet<ReportRevision> ReportRevisions => Set<ReportRevision>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<AiProviderConfiguration> AiProviders => Set<AiProviderConfiguration>();
    public DbSet<AiFeatureSettings> AiFeatureSettings => Set<AiFeatureSettings>();
    public DbSet<SensitiveWord> SensitiveWords => Set<SensitiveWord>();
    public DbSet<SensitiveScanExclusion> SensitiveScanExclusions => Set<SensitiveScanExclusion>();
    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<ScheduleDefinition> ScheduleDefinitions => Set<ScheduleDefinition>();
    public DbSet<ScheduleExecution> ScheduleExecutions => Set<ScheduleExecution>();
    public DbSet<SyncConfiguration> SyncConfigurations => Set<SyncConfiguration>();
    public DbSet<SyncEntityState> SyncEntityStates => Set<SyncEntityState>();
    public DbSet<SyncOutboxEvent> SyncOutboxEvents => Set<SyncOutboxEvent>();
    public DbSet<SyncProcessedEvent> SyncProcessedEvents => Set<SyncProcessedEvent>();
    public DbSet<SyncProcessedBatch> SyncProcessedBatches => Set<SyncProcessedBatch>();
    public DbSet<RemoteWorkEntry> RemoteWorkEntries => Set<RemoteWorkEntry>();
    public DbSet<RemoteSourceEvidence> RemoteSourceEvidence => Set<RemoteSourceEvidence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkDraft>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<WorkDraft>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<WorkEntry>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<SourceEvidence>().Property(x => x.LastObservedAt).IsConcurrencyToken();
        modelBuilder.Entity<ActivitySource>().Property(x => x.SourceType).HasConversion<string>();
        modelBuilder.Entity<ActivitySource>().Property(x => x.HealthStatus).HasConversion<string>();
        modelBuilder.Entity<SourceEvidence>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<SourceEvidence>().Property(x => x.ReachabilityStatus).HasConversion<string>();
        modelBuilder.Entity<CommitLineage>().Property(x => x.Relation).HasConversion<string>();
        modelBuilder.Entity<ReportDocument>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<ReportDocument>().Property(x => x.UpdateVersion).IsConcurrencyToken();
        modelBuilder.Entity<ScheduleDefinition>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<AiProviderConfiguration>().Property(x => x.ReasoningLevel).HasConversion<string>();

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

        modelBuilder.Entity<ReportRevision>()
            .HasIndex(x => x.ReportId)
            .IsUnique();

        modelBuilder.Entity<AiProviderConfiguration>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<AiProviderConfiguration>()
            .Property(x => x.Name)
            .UseCollation("NOCASE");

        modelBuilder.Entity<AiProviderConfiguration>()
            .HasIndex(x => x.Name)
            .IsUnique();

        modelBuilder.Entity<AiProviderConfiguration>()
            .HasIndex(x => x.IsDefault)
            .IsUnique()
            .HasFilter("\"IsDefault\" = 1");

        modelBuilder.Entity<AiFeatureSettings>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<SensitiveWord>()
            .Property(x => x.Value)
            .UseCollation("NOCASE");

        modelBuilder.Entity<SensitiveWord>()
            .HasIndex(x => x.Value)
            .IsUnique();

        modelBuilder.Entity<SensitiveScanExclusion>()
            .Property(x => x.Value)
            .UseCollation("NOCASE");

        modelBuilder.Entity<SensitiveScanExclusion>()
            .HasIndex(x => x.Value)
            .IsUnique();

        modelBuilder.Entity<PromptTemplate>()
            .HasIndex(x => x.Name);

        modelBuilder.Entity<PromptTemplate>()
            .HasIndex(x => x.IsDefault)
            .IsUnique()
            .HasFilter("\"IsDefault\" = 1");

        modelBuilder.Entity<ScheduleDefinition>()
            .HasIndex(x => x.Kind)
            .IsUnique();

        modelBuilder.Entity<ScheduleExecution>()
            .HasIndex(x => new { x.ScheduleId, x.PeriodKey })
            .IsUnique();

        modelBuilder.Entity<SyncEntityState>().HasKey(x => new { x.EntityKind, x.EntityId });
        modelBuilder.Entity<SyncOutboxEvent>().Property(x => x.EntityKind).HasConversion<string>();
        modelBuilder.Entity<SyncOutboxEvent>().Property(x => x.Operation).HasConversion<string>();
        modelBuilder.Entity<SyncOutboxEvent>().HasIndex(x => x.PublishedAt);
        modelBuilder.Entity<SyncProcessedBatch>().HasIndex(x => new { x.SyncSpaceId, x.ContentHash }).IsUnique();
        modelBuilder.Entity<RemoteWorkEntry>().HasIndex(x => new { x.OriginDeviceId, x.OriginEntityId }).IsUnique();
        modelBuilder.Entity<RemoteSourceEvidence>().Property(x => x.Kind).HasConversion<string>();
        modelBuilder.Entity<RemoteSourceEvidence>().Property(x => x.ReachabilityStatus).HasConversion<string>();
        modelBuilder.Entity<RemoteSourceEvidence>().HasIndex(x => new { x.OriginDeviceId, x.OriginEntityId }).IsUnique();
    }
}
