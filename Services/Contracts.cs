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
    string ProviderType,
    string Provider,
    string? ExecutablePath);

public sealed record AiReportRequest(
    Guid ReportId,
    string Target,
    string InputMarkdown,
    IReadOnlyList<Guid> WorkEntryIds,
    double TotalHours,
    string? ExecutablePath = null,
    string EffectivePrompt = "");

public enum AiSanitizationStatus
{
    Clean,
    Redacted,
    Failed
}

public enum AiSensitiveDataCategory
{
    Credential,
    PersonalData,
    CustomWord
}

public static class AiSanitizationRules
{
    public const string CurrentVersion = "worklens-sensitive-rules-v1";
}

public sealed record AiRedactionNotice(
    string Segment,
    AiSensitiveDataCategory Category,
    int Count);

public sealed record AiSanitizationSummary(
    AiSanitizationStatus Status,
    string ScannerVersion,
    IReadOnlyList<AiRedactionNotice> Notices,
    string? Error = null,
    string RuleVersion = AiSanitizationRules.CurrentVersion)
{
    public int TotalCount => Notices.Sum(x => x.Count);

    public bool IsReady => Status is AiSanitizationStatus.Clean or AiSanitizationStatus.Redacted;
}

public sealed class AiPreparedRequest
{
    // Keep construction inside the WorkLens assembly so provider adapters cannot accept
    // an unverified AiReportRequest. The sanitizer is the only production creator.
    internal AiPreparedRequest(
        Guid reportId,
        string target,
        string inputMarkdown,
        IReadOnlyList<Guid> workEntryIds,
        double totalHours,
        string? executablePath,
        string effectivePrompt,
        AiSanitizationSummary sanitization)
    {
        if (!sanitization.IsReady)
        {
            throw new ArgumentException("AI 請求必須先通過機敏資訊檢查。", nameof(sanitization));
        }

        ReportId = reportId;
        Target = target;
        InputMarkdown = inputMarkdown;
        WorkEntryIds = workEntryIds;
        TotalHours = totalHours;
        ExecutablePath = executablePath;
        EffectivePrompt = effectivePrompt;
        Sanitization = sanitization;
    }

    public Guid ReportId { get; }
    public string Target { get; }
    public string InputMarkdown { get; }
    public IReadOnlyList<Guid> WorkEntryIds { get; }
    public double TotalHours { get; }
    public string? ExecutablePath { get; }
    public string EffectivePrompt { get; }
    public AiSanitizationSummary Sanitization { get; }

    public AiReportRequest ToReportRequest() => new(
        ReportId,
        Target,
        InputMarkdown,
        WorkEntryIds,
        TotalHours,
        ExecutablePath,
        EffectivePrompt);
}

public sealed record AiSanitizationResult(
    AiPreparedRequest? PreparedRequest,
    AiSanitizationSummary Summary)
{
    public bool Succeeded => PreparedRequest is not null && Summary.IsReady;
}

public sealed record AiSanitizerStatus(
    bool Available,
    string Version,
    string Summary,
    string RuleVersion = AiSanitizationRules.CurrentVersion);

public sealed record AiPreparedReport(
    Guid ReportId,
    AiProviderConfiguration Configuration,
    AiPreparedRequest Request,
    Guid? PromptTemplateId,
    string PromptNameSnapshot,
    string PromptTextSnapshot);

public sealed record AiReportPreparationResult(
    AiPreparedReport? PreparedReport,
    AiSanitizationSummary Sanitization,
    string? Error)
{
    public bool Succeeded => PreparedReport is not null && Sanitization.IsReady;
}

public sealed record AiReportResult(
    bool Succeeded,
    string? Body,
    string? RawResponse,
    string? Error,
    AiSanitizationSummary? Sanitization = null);

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
        AiProviderConfiguration configuration,
        AiPreparedRequest request,
        CancellationToken cancellationToken);

    Task<AiConnectionTestResult> TestConnectionAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken);
}

public interface IAiContentSanitizer
{
    Task<AiSanitizationResult> PrepareAsync(
        AiReportRequest request,
        CancellationToken cancellationToken = default);

    Task<AiSanitizerStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);
}

public interface IAiSecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}
