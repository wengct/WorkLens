namespace WorkLens.Domain;

public enum WorkDraftKind { WorkCreate, WorkEdit, ManualCreate, ManualEdit }

public sealed class WorkDraft
{
    public string Id { get; set; } = string.Empty;
    public WorkDraftKind Kind { get; set; }
    public DateOnly Date { get; set; }
    public Guid? TargetId { get; set; }
    public string Payload { get; set; } = "{}";
    public string? BaseVersion { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string Key(WorkDraftKind kind, DateOnly date, Guid? targetId = null) =>
        targetId is { } id ? $"{kind}:{id:N}" : $"{kind}:{date:yyyy-MM-dd}";
}

// Strings preserve incomplete input (including invalid hours) while it is a draft.
public sealed record WorkDraftInput
{
    public string Date { get; init; } = string.Empty;
    public string Hours { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

public sealed record WorkDraftCommit(string Id, Guid Version);
