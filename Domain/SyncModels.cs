namespace WorkLens.Domain;

public enum SyncEntityKind { WorkEntry, SourceEvidence }
public enum SyncOperation { Upsert, Delete }

public sealed class SyncConfiguration
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public Guid Id { get; set; } = SingletonId;
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public string DeviceName { get; set; } = Environment.MachineName;
    public string? SyncSpaceId { get; set; }
    public string? FolderPath { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? LastExportedAt { get; set; }
    public DateTimeOffset? LastImportedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class SyncEntityState
{
    public string EntityKind { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public int Version { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

public sealed class SyncOutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SyncEntityKind EntityKind { get; set; }
    public Guid EntityId { get; set; }
    public int Version { get; set; }
    public SyncOperation Operation { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class SyncProcessedEvent
{
    public Guid Id { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncProcessedBatch
{
    public string Id { get; set; } = string.Empty;
    public string SyncSpaceId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class RemoteWorkEntry
{
    public Guid Id { get; set; }
    public Guid OriginDeviceId { get; set; }
    public string OriginDeviceName { get; set; } = string.Empty;
    public Guid OriginEntityId { get; set; }
    public int Version { get; set; }
    public bool IsDeleted { get; set; }
    public DateOnly WorkDate { get; set; }
    public double Hours { get; set; }
    public string Title { get; set; } = string.Empty;
    public string WorkContent { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RemoteSourceEvidence
{
    public Guid Id { get; set; }
    public Guid OriginDeviceId { get; set; }
    public string OriginDeviceName { get; set; } = string.Empty;
    public Guid OriginEntityId { get; set; }
    public int Version { get; set; }
    public bool IsDeleted { get; set; }
    public string? ProjectName { get; set; }
    public Guid SourceId { get; set; }
    public string RepositoryKey { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public EvidenceKind Kind { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string? CommitHash { get; set; }
    public string? ParentHashes { get; set; }
    public string? PatchId { get; set; }
    public string? Branch { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public CommitReachabilityStatus ReachabilityStatus { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
}