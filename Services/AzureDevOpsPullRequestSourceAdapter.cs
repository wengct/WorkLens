using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AzureDevOpsPullRequestSourceAdapter(AzureDevOpsCliService cli) : IActivitySourceAdapter
{
    public string SourceType => ActivitySourceType.AzureDevOpsPullRequest.ToString();

    public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

    public async Task<SourceValidationResult> ValidateAsync(
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeAzureDevOps(source.SettingsJson);
        if (settings.Scopes.Count == 0)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps PR 來源至少需要一組完整的 Project、Repo 與 target branch。");
        }

        if (settings.Scopes.Any(scope => !IsCompleteScope(scope)))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps PR 來源包含未完成的 Project、Repo 或 target branch 設定。");
        }

        if (settings.Scopes
            .GroupBy(SourceSettingsSerializer.AzureDevOpsScopeKey, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps PR 來源不可重複設定相同的 Project、Repo 與 target branch。");
        }

        AzureDevOpsCliDiagnostics diagnostics;
        try
        {
            diagnostics = await cli.DiagnoseAsync(settings.OrganizationUrl, cancellationToken);
        }
        catch (AzureDevOpsCliException exception)
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Error, exception.Message);
        }

        if (!diagnostics.IsValid)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Unavailable,
                diagnostics.Summary,
                diagnostics.Details.ToArray());
        }

        AzureDevOpsIdentity identity;
        try
        {
            identity = await cli.GetCurrentIdentityAsync(settings.OrganizationUrl, cancellationToken);
        }
        catch (AzureDevOpsCliException exception)
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, exception.Message);
        }

        var validScopes = 0;
        var details = new List<string> { $"登入身分：{identity.DisplayName}。" };
        if (!string.IsNullOrWhiteSpace(identity.ResolutionWarning))
        {
            details.Add(identity.ResolutionWarning);
        }
        foreach (var scope in settings.Scopes)
        {
            try
            {
                var repositories = await cli.GetRepositoriesAsync(settings.OrganizationUrl, scope.ProjectId, cancellationToken);
                var repository = repositories.FirstOrDefault(item =>
                    string.Equals(item.Id, scope.RepositoryId, StringComparison.OrdinalIgnoreCase));
                if (repository is null)
                {
                    details.Add($"{ScopeLabel(scope)}：找不到 Repo 或沒有讀取權限。");
                    continue;
                }

                var branches = await cli.GetBranchesAsync(
                    settings.OrganizationUrl,
                    scope.ProjectId,
                    scope.RepositoryId,
                    cancellationToken);
                var targetBranch = SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch);
                if (!branches.Any(branch => string.Equals(branch.Id, targetBranch, StringComparison.OrdinalIgnoreCase)))
                {
                    details.Add($"{ScopeLabel(scope)}：找不到 target branch「{targetBranch}」。");
                    continue;
                }

                validScopes++;
                details.Add($"{ScopeLabel(scope)}：設定有效。");
            }
            catch (AzureDevOpsCliException exception)
            {
                details.Add($"{ScopeLabel(scope)}：{exception.Message}");
            }
        }

        if (validScopes == settings.Scopes.Count)
        {
            return SourceValidationResult.Valid(
                $"Azure DevOps PR 來源設定有效，共 {validScopes} 組。",
                details.ToArray());
        }

        return SourceValidationResult.Invalid(
            validScopes == 0 ? SourceHealthStatus.Error : SourceHealthStatus.Ready,
            $"{validScopes}/{settings.Scopes.Count} 組 Azure DevOps PR 設定有效。",
            details.ToArray());
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeAzureDevOps(request.Source.SettingsJson);
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        if (settings.Scopes.Count == 0)
        {
            batch.Warnings.Add("Azure DevOps PR 來源尚未設定有效的收集組合。");
            return batch;
        }

        AzureDevOpsIdentity identity;
        try
        {
            identity = await cli.GetCurrentIdentityAsync(settings.OrganizationUrl, cancellationToken);
        }
        catch (AzureDevOpsCliException exception)
        {
            batch.Warnings.Add(exception.Message);
            return batch;
        }

        if (!string.IsNullOrWhiteSpace(identity.ResolutionWarning))
        {
            batch.Warnings.Add(identity.ResolutionWarning);
        }

        var checkpoint = DeserializeCheckpoint(request.Source.CheckpointJson);
        var workItemCache = new Dictionary<int, AzureDevOpsWorkItemMetadata>();
        foreach (var scope in settings.Scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCompleteScope(scope))
            {
                batch.Warnings.Add("Azure DevOps PR 來源包含未完成的 Project、Repo 或 target branch 設定。");
                continue;
            }

            var scopeKey = BuildScopeCheckpointKey(settings.OrganizationUrl, scope);
            var since = request.UpdateCheckpoint && checkpoint.LastSuccessfulAtByScope.TryGetValue(scopeKey, out var lastSuccessful)
                ? lastSuccessful.AddMinutes(-2)
                : request.Since;
            var scopeHadWarning = false;
            try
            {
                var pullRequests = await cli.ListPullRequestsAsync(
                    settings.OrganizationUrl,
                    scope,
                    identity,
                    cancellationToken);
                var scopePullRequests = pullRequests
                    .Where(pr => string.Equals(
                        SourceSettingsSerializer.NormalizeAzureDevOpsBranch(pr.TargetBranch),
                        SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var relevant = scopePullRequests
                    .Where(pr => IsWithin(pr.CreationDate, since, request.Until) || IsWithinClosedDate(pr, since, request.Until))
                    .ToList();

                var metadataCache = new Dictionary<int, AzureDevOpsPullRequestMetadata>();
                foreach (var pullRequest in relevant)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var warningCount = batch.Warnings.Count;
                    var metadata = await GetMetadataAsync(
                        settings.OrganizationUrl,
                        scope,
                        pullRequest,
                        metadataCache,
                        workItemCache,
                        batch,
                        cancellationToken);
                    scopeHadWarning |= batch.Warnings.Count > warningCount;
                    if (IsWithin(pullRequest.CreationDate, since, request.Until))
                    {
                        batch.Evidence.Add(CreateEvidence(
                            request.Source,
                            scope,
                            pullRequest,
                            metadata,
                            EvidenceKind.AzureDevOpsPullRequestCreated,
                            pullRequest.CreationDate,
                            "created"));
                    }

                    if (IsWithinClosedDate(pullRequest, since, request.Until))
                    {
                        batch.Evidence.Add(CreateEvidence(
                            request.Source,
                            scope,
                            pullRequest,
                            metadata,
                            EvidenceKind.AzureDevOpsPullRequestClosed,
                            pullRequest.ClosedDate!.Value,
                            "closed"));
                    }
                }

                if (!scopeHadWarning && request.UpdateCheckpoint)
                {
                    checkpoint.LastSuccessfulAtByScope[scopeKey] = DateTimeOffset.UtcNow;
                }

                if (!scopeHadWarning)
                {
                    batch.SuccessfulRepositories++;
                }
            }
            catch (AzureDevOpsCliException exception)
            {
                batch.Warnings.Add($"{ScopeLabel(scope)}：{exception.Message}");
            }
        }

        if (request.UpdateCheckpoint)
        {
            batch.CheckpointJson = JsonSerializer.Serialize(checkpoint);
        }

        return batch;
    }

    private async Task<AzureDevOpsPullRequestMetadata> GetMetadataAsync(
        string organizationUrl,
        AzureDevOpsPullRequestScope scope,
        AzureDevOpsPullRequestRecord pullRequest,
        IDictionary<int, AzureDevOpsPullRequestMetadata> metadataCache,
        IDictionary<int, AzureDevOpsWorkItemMetadata> workItemCache,
        CollectionBatch batch,
        CancellationToken cancellationToken)
    {
        if (metadataCache.TryGetValue(pullRequest.Id, out var cached))
        {
            return cached;
        }

        var metadata = new AzureDevOpsPullRequestMetadata
        {
            PullRequestId = pullRequest.Id,
            OrganizationUrl = SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(organizationUrl),
            ProjectId = scope.ProjectId,
            ProjectName = pullRequest.ProjectName,
            RepositoryName = pullRequest.RepositoryName,
            Url = pullRequest.Url,
            Status = pullRequest.Status,
            IsDraft = pullRequest.IsDraft,
            CreatorId = pullRequest.CreatorId,
            CreatorName = pullRequest.CreatorName,
            CreatorUniqueName = pullRequest.CreatorUniqueName,
            SourceBranch = pullRequest.SourceBranch,
            TargetBranch = pullRequest.TargetBranch,
            CreationDate = pullRequest.CreationDate,
            ClosedDate = pullRequest.ClosedDate,
            Description = pullRequest.Description
        };

        try
        {
            var references = await cli.ListPullRequestWorkItemsAsync(
                organizationUrl,
                pullRequest.Id,
                cancellationToken);
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!workItemCache.TryGetValue(reference.Id, out var workItem))
                {
                    try
                    {
                        workItem = await cli.GetWorkItemAsync(organizationUrl, reference, cancellationToken);
                        workItemCache[reference.Id] = workItem;
                    }
                    catch (AzureDevOpsCliException exception)
                    {
                        batch.Warnings.Add($"PR #{pullRequest.Id} 的 work item #{reference.Id}：{exception.Message}");
                        continue;
                    }
                }

                metadata.WorkItems.Add(workItem);
            }
        }
        catch (AzureDevOpsCliException exception)
        {
            batch.Warnings.Add($"PR #{pullRequest.Id}：{exception.Message}");
        }

        metadataCache[pullRequest.Id] = metadata;
        return metadata;
    }

    private static SourceEvidence CreateEvidence(
        ActivitySource source,
        AzureDevOpsPullRequestScope scope,
        AzureDevOpsPullRequestRecord pullRequest,
        AzureDevOpsPullRequestMetadata metadata,
        EvidenceKind kind,
        DateTimeOffset occurredAt,
        string eventName)
    {
        var repositoryKey = BuildRepositoryKey(metadata.OrganizationUrl, scope);
        return new SourceEvidence
        {
            SourceId = source.Id,
            ProjectId = scope.WorkLensProjectId,
            RepositoryKey = repositoryKey,
            RepositoryPath = $"{metadata.OrganizationUrl}/{scope.ProjectName}/{scope.RepositoryName}",
            Environment = "Azure DevOps",
            Kind = kind,
            ExternalKey = $"{repositoryKey}:pr:{pullRequest.Id}:{eventName}",
            Title = $"PR #{pullRequest.Id}｜{pullRequest.Title}",
            CommitMessage = AzureDevOpsCliService.ToPlainText(pullRequest.Description),
            OccurredAt = occurredAt,
            Branch = SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch),
            MetadataJson = SourceSettingsSerializer.SerializeAzureDevOpsMetadata(metadata),
            ReachabilityStatus = CommitReachabilityStatus.Unknown
        };
    }

    private static bool IsWithin(DateTimeOffset value, DateTimeOffset since, DateTimeOffset? until) =>
        value >= since && (until is null || value < until.Value);

    private static bool IsWithinClosedDate(
        AzureDevOpsPullRequestRecord pullRequest,
        DateTimeOffset since,
        DateTimeOffset? until) =>
        pullRequest.ClosedDate is DateTimeOffset closedDate &&
        IsClosedPullRequestStatus(pullRequest.Status) &&
        IsWithin(closedDate, since, until);

    private static bool IsClosedPullRequestStatus(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "abandoned", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleteScope(AzureDevOpsPullRequestScope scope) =>
        !string.IsNullOrWhiteSpace(scope.ProjectId) &&
        !string.IsNullOrWhiteSpace(scope.RepositoryId) &&
        !string.IsNullOrWhiteSpace(SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch));

    private static string BuildRepositoryKey(string organizationUrl, AzureDevOpsPullRequestScope scope)
    {
        var organization = new Uri(organizationUrl);
        var organizationPath = organization.AbsolutePath.Trim('/');
        var organizationKey = string.IsNullOrWhiteSpace(organizationPath)
            ? organization.Host
            : $"{organization.Host}/{organizationPath}";

        return $"ado:{organizationKey.ToLowerInvariant()}:{scope.ProjectId.Trim()}:{scope.RepositoryId.Trim()}";
    }

    private static string BuildScopeCheckpointKey(string organizationUrl, AzureDevOpsPullRequestScope scope) =>
        $"{SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(organizationUrl)}|{SourceSettingsSerializer.AzureDevOpsScopeKey(scope)}";

    private static string ScopeLabel(AzureDevOpsPullRequestScope scope) =>
        $"{scope.ProjectName}/{scope.RepositoryName}/{SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch)}";

    private static AzureDevOpsCheckpoint DeserializeCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AzureDevOpsCheckpoint();
        }

        try
        {
            var checkpoint = JsonSerializer.Deserialize<AzureDevOpsCheckpoint>(json);
            checkpoint ??= new AzureDevOpsCheckpoint();
            checkpoint.LastSuccessfulAtByScope ??= new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            return checkpoint;
        }
        catch (JsonException)
        {
            return new AzureDevOpsCheckpoint();
        }
    }

    private sealed class AzureDevOpsCheckpoint
    {
        public Dictionary<string, DateTimeOffset> LastSuccessfulAtByScope { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
