namespace WorkLens.Domain;

public enum ActivitySourceType
{
    WindowsGit,
    WslGit,
    WindowsCodex,
    WslCodex,
    MacOsGit,
    MacOsCodex,
    AzureDevOpsPullRequest,
    WindowsClaudeCode,
    WslClaudeCode,
    MacOsClaudeCode,
    WindowsCopilot,
    WslCopilot,
    MacOsCopilot,
    WindowsVsCodeCopilot,
    WslVsCodeCopilot,
    MacOsVsCodeCopilot,
    WindowsVisualStudioCopilot
}

public enum SourceHealthStatus
{
    Disabled,
    Ready,
    Unavailable,
    Running,
    Error
}

public enum EvidenceKind
{
    Manual,
    Commit,
    WorkingTreeSnapshot,
    RebaseStarted,
    RebaseStep,
    RebaseFinished,
    RebaseAborted,
    RewriteDetected,
    ConflictObserved,
    BranchMoved,
    ReflogActivity,
    CodexSession,
    AzureDevOpsPullRequestCreated,
    AzureDevOpsPullRequestClosed,
    ClaudeCodeSession,
    CopilotSession
}

public enum CommitReachabilityStatus
{
    Current,
    Provisional,
    Superseded,
    Dropped,
    Unknown
}

public enum CommitLineageRelation
{
    Rebased,
    Amended,
    SquashedInto,
    Dropped,
    UnknownRewrite
}

public enum ReportKind
{
    Daily,
    Weekly
}

public enum ScheduleKind
{
    DailyReport,
    WeeklyReport,
    DailyBackup,
    WeeklyBackup
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6d7cff";
    public bool IncludeInAi { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly WorkDate { get; set; }
    public double Hours { get; set; }
    public string Title { get; set; } = string.Empty;
    public string WorkContent { get; set; } = string.Empty;

    public Guid? ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ActivitySource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public ActivitySourceType SourceType { get; set; }
    public bool Enabled { get; set; }
    public bool IsArchived { get; set; }
    public int CollectionIntervalMinutes { get; set; } = 10;
    public int InitialImportDays { get; set; } = 7;
    public Guid? ProjectId { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public bool IncludeInAi { get; set; }
    public SourceHealthStatus HealthStatus { get; set; } = SourceHealthStatus.Disabled;
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public string CheckpointJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SourceEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Guid? ProjectId { get; set; }
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
    public CommitReachabilityStatus ReachabilityStatus { get; set; } = CommitReachabilityStatus.Unknown;
    public DateTimeOffset FirstObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CommitLineage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public string RepositoryKey { get; set; } = string.Empty;
    public string OldCommitHash { get; set; } = string.Empty;
    public string NewCommitHash { get; set; } = string.Empty;
    public CommitLineageRelation Relation { get; set; }
    public string Confidence { get; set; } = "inferred";
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RebaseSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public string RepositoryKey { get; set; } = string.Empty;
    public string ExternalKey { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "InProgress";
    public string? OriginalHead { get; set; }
    public string? NewHead { get; set; }
}

public sealed class ReportDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ReportKind Kind { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public double TotalHours { get; set; }
    public string DeterministicBody { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsStale { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public Guid? AiJobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderType { get; set; } = "ask-bridge";
    public string Provider { get; set; } = "chatgpt";
    public string Status { get; set; } = "Pending";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
    public Guid? PromptTemplateId { get; set; }
    public string PromptNameSnapshot { get; set; } = string.Empty;
    public string PromptTextSnapshot { get; set; } = string.Empty;
    public string SanitizationStatus { get; set; } = "NotRun";
    public int SanitizedFindingCount { get; set; }
    public string SanitizedCategoriesJson { get; set; } = "[]";
    public string SanitizerVersion { get; set; } = string.Empty;
    public string SanitizerRuleVersion { get; set; } = string.Empty;
}

public sealed class AiProviderConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public string ProviderType { get; set; } = "ask-bridge";
    public bool UseHeadless { get; set; } = true;
    public string Provider { get; set; } = "chatgpt";
    public string? ExecutablePath { get; set; }
    public string? ProtectedApiKey { get; set; }
    public string? ApiEndpoint { get; set; }
    public string? Model { get; set; }
    public string? ApiVersion { get; set; }
    public AiReasoningLevel ReasoningLevel { get; set; }
    public string Status { get; set; } = "NotConfigured";
    public string? DetectedVersion { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string GeneralReportPrompt { get; set; } = AiPromptDefaults.GeneralReportPrompt;
    public string DailyReportPromptOverride { get; set; } = string.Empty;
    public string WeeklyReportPromptOverride { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiFeatureSettings
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = SingletonId;
    public bool Enabled { get; set; }
}

public sealed class SensitiveWord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Value { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AiReasoningLevel
{
    Default,
    Low,
    Medium,
    High
}

public static class AiPromptDefaults
{
    public const string GeneralReportPrompt = """
        請將資料整理成清楚、可直接交付的工作回報，並依據工作內容自動歸類，按以下固定分類拆分章節：專案管理、UIUX相關、需求評估、功能開發、功能測試、BUG處理、文件相關、客服、其他。分類名稱、文字與順序不可更動；只建立有內容的章節。同一筆工作若涉及多個分類，歸入最主要的分類，避免重複。保留具體成果與處理過程；以繁體中文撰寫，內容精簡但不可遺漏重要脈絡。

        格式如下：

        # 每日工作回報

        ## 日期：{{YYYY-MM-DD}}

        ## 專案：{{專案1}}

        ## 工作項目：{{專案1}}

        ### 專案管理

        - ...

        ### UIUX相關

        - ...

        ## 專案：{{專案2}}

        ## 工作項目

        ### 專案管理

        - ...

        ### UIUX相關

        - ...
        """;
}

public sealed class PromptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ScheduleDefinition
{
    public Guid Id { get; set; }
    public ScheduleKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public int DaysOfWeekMask { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public Guid? PromptTemplateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ScheduleExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public string PeriodKey { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public string Status { get; set; } = "Running";
    public string DeterministicStatus { get; set; } = "NotApplicable";
    public string AiStatus { get; set; } = "NotApplicable";
    public string BackupStatus { get; set; } = "NotApplicable";
    public Guid? PromptTemplateId { get; set; }
    public string PromptNameSnapshot { get; set; } = string.Empty;
    public string PromptTextSnapshot { get; set; } = string.Empty;
    public string SanitizationStatus { get; set; } = "NotRun";
    public int SanitizedFindingCount { get; set; }
    public string SanitizedCategoriesJson { get; set; } = "[]";
    public string SanitizerVersion { get; set; } = string.Empty;
    public string SanitizerRuleVersion { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class BackupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "Daily";
    public string PeriodKey { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IntegrityChecked { get; set; }
    public string? Error { get; set; }
}
