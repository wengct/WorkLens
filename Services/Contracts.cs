using WorkLens.Domain;

namespace WorkLens.Services;

public sealed record SourceCapabilities(
    bool SupportsHistory = false,
    bool SupportsWorkingTree = false,
    bool SupportsRewrites = false);

public sealed record SourceDefinition(
    ActivitySourceType SourceType,
    string DisplayName,
    SourceCapabilities Capabilities);

public sealed record SourceValidationResult(
    bool IsValid,
    SourceHealthStatus Status,
    string Summary,
    IReadOnlyList<string> Details)
{
    public static SourceValidationResult Valid(string summary, params string[] details) =>
        new(true, SourceHealthStatus.Ready, summary, details);

    public static SourceValidationResult Invalid(SourceHealthStatus status, string summary, params string[] details) =>
        new(false, status, summary, details);
}

public sealed record CollectionRequest(
    ActivitySource Source,
    DateTimeOffset Since,
    DateTimeOffset? Until = null,
    bool UpdateCheckpoint = true,
    CancellationToken CancellationToken = default);

public sealed class CollectionBatch
{
    public List<SourceEvidence> Evidence { get; } = [];
    public List<CommitLineageCandidate> LineageCandidates { get; } = [];
    public Dictionary<string, HashSet<string>> CurrentCommitsByRepository { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
    public int SuccessfulRepositories { get; set; }
    public string CheckpointJson { get; set; } = "{}";
}

public sealed record CommitLineageCandidate(
    string RepositoryKey,
    string OldCommitHash,
    string NewCommitHash,
    CommitLineageRelation Relation,
    string Confidence);

public sealed record AiProviderValidationResult(
    bool IsValid,
    string Status,
    string Summary,
    string? ExecutablePath = null,
    string? Version = null,
    IReadOnlyList<string>? Details = null);

public sealed record AiProviderConfigurationSnapshot(
    bool Enabled,
    string Provider,
    string? ExecutablePath);

public sealed record AiReportRequest(
    Guid ReportId,
    string Provider,
    string InputMarkdown,
    IReadOnlyList<Guid> WorkEntryIds,
    double TotalHours,
    string? ExecutablePath = null,
    string EffectivePrompt = "");

public sealed record AiReportResult(
    bool Succeeded,
    string? Body,
    string? RawResponse,
    string? Error);

public sealed record AiConnectionTestResult(
    bool Succeeded,
    string? RawResponse,
    string? Error);

public sealed record AiDetectionResult(
    AiProviderValidationResult Validation,
    string? ExecutablePath,
    string? Version,
    bool NodeAvailable,
    bool ChromeAvailable);

public interface IAiProviderAdapter
{
    string ProviderType { get; }

    Task<AiProviderValidationResult> ValidateAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken);

    Task<AiReportResult> GenerateAsync(
        AiReportRequest request,
        CancellationToken cancellationToken);
}
