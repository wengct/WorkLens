using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Domain;

public sealed class GitSourceSettings
{
    public List<string> RepositoryPaths { get; set; } = [];
    public List<string> AuthorEmails { get; set; } = [];
    public string? Distro { get; set; }
}

public sealed class CodexSourceSettings
{
    public string? CodexHome { get; set; }
    public string? Distro { get; set; }
}

public sealed class ClaudeCodeSourceSettings
{
    public string? ClaudeCodeHome { get; set; }
    public string? Distro { get; set; }
}

public sealed class CopilotSourceSettings
{
    public string? CopilotHome { get; set; }
    public string? Distro { get; set; }
}

public sealed class VsCodeCopilotSourceSettings
{
    public List<string> WorkspaceStoragePaths { get; set; } = [];
    public string? Distro { get; set; }
}

public sealed class VisualStudioCopilotSourceSettings
{
    public List<string> SolutionPaths { get; set; } = [];
}

public sealed class AzureDevOpsSourceSettings
{
    public string OrganizationUrl { get; set; } = string.Empty;
    // Kept for backward-compatible PR source settings persisted by previous releases.
    public List<AzureDevOpsPullRequestScope> Scopes { get; set; } = [];
    public bool CollectPullRequests { get; set; } = true;
    public bool CollectWorkItems { get; set; }
    public List<AzureDevOpsWorkItemScope> WorkItemScopes { get; set; } = [];
}

public sealed class AzureDevOpsPullRequestScope
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public Guid? WorkLensProjectId { get; set; }
}

public sealed class AzureDevOpsWorkItemScope
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public Guid? WorkLensProjectId { get; set; }
}

public sealed class AzureDevOpsPullRequestMetadata
{
    public int PullRequestId { get; set; }
    public string OrganizationUrl { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsDraft { get; set; }
    public string CreatorId { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorUniqueName { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public DateTimeOffset CreationDate { get; set; }
    public DateTimeOffset? ClosedDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<AzureDevOpsWorkItemMetadata> WorkItems { get; set; } = [];
}

public sealed class AzureDevOpsWorkItemMetadata
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AcceptanceCriteria { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AzureDevOpsWorkItemRelation> Relations { get; set; } = [];
}

public sealed class AzureDevOpsWorkItemRelation
{
    public string Rel { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? AttributesJson { get; set; }
}

public sealed class AzureDevOpsWorkItemActivityMetadata
{
    public string OrganizationUrl { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public AzureDevOpsWorkItemMetadata WorkItem { get; set; } = new();
    public List<AzureDevOpsWorkItemFieldChange> FieldChanges { get; set; } = [];
    public List<AzureDevOpsWorkItemDiscussion> Discussions { get; set; } = [];
}

public sealed class AzureDevOpsWorkItemFieldChange
{
    public DateTimeOffset ChangedAt { get; set; }
    public string Field { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
}

public sealed class AzureDevOpsWorkItemDiscussion
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool IsEdited { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class CodexSessionMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Cwd { get; set; }
    public string Platform { get; set; } = string.Empty;
    public bool Archived { get; set; }
    public string? CliVersion { get; set; }
    public int AttachmentCount { get; set; }
    public List<CodexSessionMessage> Messages { get; set; } = [];
}

public sealed class CodexSessionMessage
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class ClaudeCodeSessionMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Cwd { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string? CliVersion { get; set; }
    public int AttachmentCount { get; set; }
    public List<ClaudeCodeSessionMessage> Messages { get; set; } = [];
}

public sealed class ClaudeCodeSessionMessage
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class CopilotSessionMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Cwd { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string SourceFormat { get; set; } = string.Empty;
    public string Client { get; set; } = "GitHub Copilot";
    public string? SourcePath { get; set; }
    public bool IsComplete { get; set; } = true;
    public List<CopilotSessionMessage> Messages { get; set; } = [];
}

public sealed class CopilotSessionMessage
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;
}

public static class SourceSettingsSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static GitSourceSettings DeserializeGit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GitSourceSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<GitSourceSettings>(json, Options)
                ?? new GitSourceSettings();
            settings.RepositoryPaths ??= [];
            settings.AuthorEmails ??= [];
            return settings;
        }
        catch (JsonException)
        {
            return new GitSourceSettings();
        }
    }

    public static string Serialize(GitSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static CodexSourceSettings DeserializeCodex(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CodexSourceSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<CodexSourceSettings>(json, Options)
                ?? new CodexSourceSettings();
        }
        catch (JsonException)
        {
            return new CodexSourceSettings();
        }
    }

    public static string Serialize(CodexSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static ClaudeCodeSourceSettings DeserializeClaudeCode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ClaudeCodeSourceSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ClaudeCodeSourceSettings>(json, Options)
                ?? new ClaudeCodeSourceSettings();
        }
        catch (JsonException)
        {
            return new ClaudeCodeSourceSettings();
        }
    }

    public static string Serialize(ClaudeCodeSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static CopilotSourceSettings DeserializeCopilot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CopilotSourceSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<CopilotSourceSettings>(json, Options)
                ?? new CopilotSourceSettings();
        }
        catch (JsonException)
        {
            return new CopilotSourceSettings();
        }
    }

    public static string Serialize(CopilotSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static VsCodeCopilotSourceSettings DeserializeVsCodeCopilot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VsCodeCopilotSourceSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<VsCodeCopilotSourceSettings>(json, Options)
                ?? new VsCodeCopilotSourceSettings();
            settings.WorkspaceStoragePaths ??= [];
            return settings;
        }
        catch (JsonException)
        {
            return new VsCodeCopilotSourceSettings();
        }
    }

    public static string Serialize(VsCodeCopilotSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static VisualStudioCopilotSourceSettings DeserializeVisualStudioCopilot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VisualStudioCopilotSourceSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<VisualStudioCopilotSourceSettings>(json, Options)
                ?? new VisualStudioCopilotSourceSettings();
            settings.SolutionPaths ??= [];
            return settings;
        }
        catch (JsonException)
        {
            return new VisualStudioCopilotSourceSettings();
        }
    }

    public static string Serialize(VisualStudioCopilotSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static AzureDevOpsSourceSettings DeserializeAzureDevOps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AzureDevOpsSourceSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AzureDevOpsSourceSettings>(json, Options)
                ?? new AzureDevOpsSourceSettings();
            settings.Scopes ??= [];
            settings.WorkItemScopes ??= [];
            // A setting persisted before the Work Item feature has no boolean properties;
            // treat it as the legacy PR-only collector rather than changing its behavior.
            if (!json.Contains("collectPullRequests", StringComparison.OrdinalIgnoreCase))
            {
                settings.CollectPullRequests = true;
            }
            if (!json.Contains("collectWorkItems", StringComparison.OrdinalIgnoreCase))
            {
                settings.CollectWorkItems = false;
            }
            settings.Scopes.RemoveAll(scope => scope is null);
            foreach (var scope in settings.Scopes)
            {
                scope.ProjectId ??= string.Empty;
                scope.ProjectName ??= string.Empty;
                scope.RepositoryId ??= string.Empty;
                scope.RepositoryName ??= string.Empty;
                scope.TargetBranch ??= string.Empty;
            }
            settings.WorkItemScopes.RemoveAll(scope => scope is null);
            foreach (var scope in settings.WorkItemScopes)
            {
                scope.ProjectId ??= string.Empty;
                scope.ProjectName ??= string.Empty;
            }
            return settings;
        }
        catch (JsonException)
        {
            return new AzureDevOpsSourceSettings();
        }
    }

    public static string Serialize(AzureDevOpsSourceSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    public static string SerializeAzureDevOpsMetadata(AzureDevOpsPullRequestMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    public static AzureDevOpsPullRequestMetadata? DeserializeAzureDevOpsMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<AzureDevOpsPullRequestMetadata>(json, Options);
            if (metadata is null)
            {
                return null;
            }

            metadata.WorkItems ??= [];
            metadata.WorkItems.RemoveAll(workItem => workItem is null);
            foreach (var workItem in metadata.WorkItems)
            {
                workItem.Fields ??= new(StringComparer.OrdinalIgnoreCase);
                workItem.Relations ??= [];
                workItem.Relations.RemoveAll(relation => relation is null);
            }

            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeAzureDevOpsWorkItemActivityMetadata(AzureDevOpsWorkItemActivityMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    public static AzureDevOpsWorkItemActivityMetadata? DeserializeAzureDevOpsWorkItemActivityMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<AzureDevOpsWorkItemActivityMetadata>(json, Options);
            if (metadata is null) return null;
            metadata.WorkItem ??= new AzureDevOpsWorkItemMetadata();
            metadata.WorkItem.Fields ??= new(StringComparer.OrdinalIgnoreCase);
            metadata.WorkItem.Relations ??= [];
            metadata.FieldChanges ??= [];
            metadata.Discussions ??= [];
            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NormalizeAzureDevOpsOrganizationUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized;
    }

    public static string NormalizeAzureDevOpsBranch(string? value)
    {
        var branch = (value ?? string.Empty).Trim();
        if (branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
        {
            branch = branch["refs/heads/".Length..];
        }

        return branch.Trim('/');
    }

    public static string AzureDevOpsScopeKey(AzureDevOpsPullRequestScope scope) =>
        $"{(scope.ProjectId ?? string.Empty).Trim()}|{(scope.RepositoryId ?? string.Empty).Trim()}|{NormalizeAzureDevOpsBranch(scope.TargetBranch)}";

    public static string AzureDevOpsWorkItemScopeKey(AzureDevOpsWorkItemScope scope) =>
        (scope.ProjectId ?? string.Empty).Trim();

    public static string SerializeMetadata(CodexSessionMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    public static CodexSessionMetadata? DeserializeCodexMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodexSessionMetadata>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeClaudeCodeMetadata(ClaudeCodeSessionMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    public static ClaudeCodeSessionMetadata? DeserializeClaudeCodeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<ClaudeCodeSessionMetadata>(json, Options);
            if (metadata is null)
            {
                return null;
            }

            metadata.Messages ??= [];
            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeCopilotMetadata(CopilotSessionMetadata metadata) =>
        JsonSerializer.Serialize(metadata, Options);

    public static CopilotSessionMetadata? DeserializeCopilotMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<CopilotSessionMetadata>(json, Options);
            if (metadata is null)
            {
                return null;
            }

            metadata.Messages ??= [];
            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> NormalizeAuthorEmails(IEnumerable<string>? emails) =>
        (emails ?? Enumerable.Empty<string>())
            .Select(email => email.Trim())
            .Where(email => email.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsValidAuthorEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var value = email.Trim();
        var at = value.IndexOf('@');
        return value.Length <= 320 &&
               !value.Any(char.IsWhiteSpace) &&
               at > 0 &&
               at == value.LastIndexOf('@') &&
               at < value.Length - 1;
    }
}
