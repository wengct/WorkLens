using System.Text.Json;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AzureDevOpsPullRequestSourceAdapter(AzureDevOpsCliService cli) : IActivitySourceAdapter
{
    private const int WorkItemRequestConcurrency = 6;

    public string SourceType => ActivitySourceType.AzureDevOpsPullRequest.ToString();

    public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

    public async Task<SourceValidationResult> ValidateAsync(
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeAzureDevOps(source.SettingsJson);
        if (!settings.CollectPullRequests && !settings.CollectWorkItems)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps 來源至少要啟用 PR 或 Work Item 收集。");
        }

        if (settings.CollectPullRequests && (settings.Scopes.Count == 0 || settings.Scopes.Any(scope => !IsCompleteScope(scope))))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps PR 收集包含未完成的 Project、Repo 或 target branch 設定。");
        }

        if (settings.CollectPullRequests && settings.Scopes
            .GroupBy(SourceSettingsSerializer.AzureDevOpsScopeKey, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps PR 來源不可重複設定相同的 Project、Repo 與 target branch。");
        }
        if (settings.CollectWorkItems && (settings.WorkItemScopes.Count == 0 || settings.WorkItemScopes.Any(scope => !IsCompleteWorkItemScope(scope))))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps Work Item 收集至少需要一組完整的 Project 設定。");
        }
        if (settings.CollectWorkItems && settings.WorkItemScopes
            .GroupBy(SourceSettingsSerializer.AzureDevOpsWorkItemScopeKey, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Azure DevOps Work Item 收集不可重複設定相同的 Project。");
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
        foreach (var scope in settings.CollectPullRequests ? settings.Scopes : [])
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

        foreach (var scope in settings.CollectWorkItems ? settings.WorkItemScopes : [])
        {
            try
            {
                _ = await cli.ListChangedWorkItemsAsync(
                    settings.OrganizationUrl,
                    scope,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                validScopes++;
                details.Add($"{WorkItemScopeLabel(scope)}：設定有效。");
            }
            catch (AzureDevOpsCliException exception)
            {
                details.Add($"{WorkItemScopeLabel(scope)}：{exception.Message}");
            }
        }

        var configuredScopeCount = (settings.CollectPullRequests ? settings.Scopes.Count : 0) +
                                   (settings.CollectWorkItems ? settings.WorkItemScopes.Count : 0);
        if (validScopes == configuredScopeCount)
        {
            return SourceValidationResult.Valid(
                $"Azure DevOps 來源設定有效，共 {validScopes} 組。",
                details.ToArray());
        }

        return SourceValidationResult.Invalid(
            validScopes == 0 ? SourceHealthStatus.Error : SourceHealthStatus.Ready,
            $"{validScopes}/{configuredScopeCount} 組 Azure DevOps 設定有效。",
            details.ToArray());
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeAzureDevOps(request.Source.SettingsJson);
        var batch = new CollectionBatch { CheckpointJson = request.Source.CheckpointJson };
        if (!settings.CollectPullRequests && !settings.CollectWorkItems)
        {
            batch.Warnings.Add("Azure DevOps 來源尚未啟用任何收集項目。");
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
        foreach (var scope in settings.CollectPullRequests ? settings.Scopes : [])
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

        foreach (var scope in settings.CollectWorkItems ? settings.WorkItemScopes : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCompleteWorkItemScope(scope))
            {
                batch.Warnings.Add("Azure DevOps Work Item 收集包含未完成的 Project 設定。");
                continue;
            }

            var scopeKey = BuildWorkItemScopeCheckpointKey(settings.OrganizationUrl, scope);
            var since = request.UpdateCheckpoint && checkpoint.LastSuccessfulAtByScope.TryGetValue(scopeKey, out var lastSuccessful)
                ? lastSuccessful.AddMinutes(-2)
                : request.Since;
            try
            {
                var references = await cli.ListChangedWorkItemsAsync(
                    settings.OrganizationUrl,
                    scope,
                    since,
                    request.Until,
                    cancellationToken);
                var updates = await SelectBoundedAsync(
                    references,
                    WorkItemRequestConcurrency,
                    reference => cli.GetWorkItemUpdatesAsync(settings.OrganizationUrl, scope, reference.Id, cancellationToken),
                    cancellationToken);
                var activityCandidates = references
                    .Zip(updates)
                    .Where(pair => MayContainCurrentUserWorkItemActivity(pair.Second, identity, since, request.Until))
                    .Select(pair => new WorkItemActivityCandidate(pair.First, pair.Second))
                    .ToList();
                var collectedItems = await SelectBoundedAsync(
                    activityCandidates,
                    WorkItemRequestConcurrency,
                    async candidate =>
                    {
                        var workItemTask = cli.GetWorkItemAsync(settings.OrganizationUrl, candidate.Reference, cancellationToken);
                        var commentsTask = cli.GetWorkItemCommentsAsync(settings.OrganizationUrl, scope, candidate.Reference.Id, cancellationToken);
                        await Task.WhenAll(workItemTask, commentsTask);
                        return new CollectedWorkItemActivity(
                            await workItemTask,
                            candidate.Updates,
                            await commentsTask);
                    },
                    cancellationToken);
                foreach (var collected in collectedItems)
                {
                    foreach (var evidence in CreateWorkItemEvidence(
                                 request.Source,
                                 settings.OrganizationUrl,
                                 scope,
                                 collected.WorkItem,
                                 collected.Updates,
                                 collected.Comments,
                                 identity,
                                 since,
                                 request.Until))
                    {
                        batch.Evidence.Add(evidence);
                    }
                }

                if (request.UpdateCheckpoint)
                {
                    checkpoint.LastSuccessfulAtByScope[scopeKey] = DateTimeOffset.UtcNow;
                }
                batch.SuccessfulRepositories++;
            }
            catch (AzureDevOpsCliException exception)
            {
                batch.Warnings.Add($"{WorkItemScopeLabel(scope)}：{exception.Message}");
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

    private static IReadOnlyList<SourceEvidence> CreateWorkItemEvidence(
        ActivitySource source,
        string organizationUrl,
        AzureDevOpsWorkItemScope scope,
        AzureDevOpsWorkItemMetadata workItem,
        IReadOnlyList<AzureDevOpsWorkItemUpdate> updates,
        IReadOnlyList<AzureDevOpsWorkItemComment> comments,
        AzureDevOpsIdentity identity,
        DateTimeOffset since,
        DateTimeOffset? until)
    {
        var daily = new Dictionary<DateOnly, AzureDevOpsWorkItemActivityMetadata>();
        foreach (var update in updates)
        {
            if (IsPlaceholderTimestamp(update.RevisedDate) ||
                !IsWithin(update.RevisedDate, since, until) ||
                !AzureDevOpsCliService.IsIdentityMatch(update.RevisedBy, identity))
            {
                continue;
            }

            var changes = update.Fields
                .Where(change => !IsAuditField(change.Field))
                .Select(change => new AzureDevOpsWorkItemFieldChange
                {
                    ChangedAt = update.RevisedDate,
                    Field = change.Field,
                    OldValue = change.OldValue,
                    NewValue = change.NewValue
                })
                .ToList();
            if (changes.Count == 0)
            {
                continue;
            }

            var metadata = GetDailyMetadata(daily, update.RevisedDate, organizationUrl, scope, workItem);
            metadata.FieldChanges.AddRange(changes);
        }

        foreach (var comment in comments)
        {
            if (comment.IsDeleted || IsPlaceholderTimestamp(comment.CreatedDate))
            {
                continue;
            }

            if (AzureDevOpsCliService.IsIdentityMatch(comment.CreatedBy, identity) &&
                IsWithin(comment.CreatedDate, since, until))
            {
                AddDiscussion(GetDailyMetadata(daily, comment.CreatedDate, organizationUrl, scope, workItem), new AzureDevOpsWorkItemDiscussion
                    {
                        Id = comment.Id,
                        OccurredAt = comment.CreatedDate,
                        Text = comment.Text
                    });
            }

            if (comment.ModifiedDate is DateTimeOffset modifiedDate &&
                modifiedDate > comment.CreatedDate &&
                !IsPlaceholderTimestamp(modifiedDate) &&
                AzureDevOpsCliService.IsIdentityMatch(comment.ModifiedBy, identity) &&
                IsWithin(modifiedDate, since, until))
            {
                AddDiscussion(GetDailyMetadata(daily, modifiedDate, organizationUrl, scope, workItem), new AzureDevOpsWorkItemDiscussion
                    {
                        Id = comment.Id,
                        OccurredAt = modifiedDate,
                        IsEdited = true,
                        Text = comment.Text
                    });
            }
        }

        var repositoryKey = BuildWorkItemRepositoryKey(organizationUrl, scope);
        return daily
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                var metadata = pair.Value;
                metadata.FieldChanges = metadata.FieldChanges.OrderBy(change => change.ChangedAt).ToList();
                metadata.Discussions = metadata.Discussions.OrderBy(discussion => discussion.OccurredAt).ToList();
                var occurredAt = metadata.FieldChanges.Select(change => change.ChangedAt)
                    .Concat(metadata.Discussions.Select(discussion => discussion.OccurredAt))
                    .Max();
                return new SourceEvidence
                {
                    SourceId = source.Id,
                    ProjectId = scope.WorkLensProjectId,
                    RepositoryKey = repositoryKey,
                    RepositoryPath = $"{metadata.OrganizationUrl}/{scope.ProjectName}",
                    Environment = "Azure DevOps",
                    Kind = EvidenceKind.AzureDevOpsWorkItemActivity,
                    ExternalKey = $"{repositoryKey}:work-item:{workItem.Id}:{pair.Key:yyyy-MM-dd}",
                    Title = $"Work Item #{workItem.Id}｜{workItem.Title}",
                    CommitMessage = BuildWorkItemActivityMessage(metadata),
                    OccurredAt = occurredAt,
                    MetadataJson = SourceSettingsSerializer.SerializeAzureDevOpsWorkItemActivityMetadata(metadata),
                    ReachabilityStatus = CommitReachabilityStatus.Unknown
                };
            })
            .ToList();
    }

    private static bool MayContainCurrentUserWorkItemActivity(
        IReadOnlyList<AzureDevOpsWorkItemUpdate> updates,
        AzureDevOpsIdentity identity,
        DateTimeOffset since,
        DateTimeOffset? until) =>
        updates.Any(update =>
            AzureDevOpsCliService.IsIdentityMatch(update.RevisedBy, identity) &&
            (update.Fields.Any(change => string.Equals(change.Field, "System.History", StringComparison.OrdinalIgnoreCase)) ||
             (!IsPlaceholderTimestamp(update.RevisedDate) &&
              IsWithin(update.RevisedDate, since, until) &&
              update.Fields.Any(change => !IsAuditField(change.Field)))));

    private static async Task<IReadOnlyList<TResult>> SelectBoundedAsync<TSource, TResult>(
        IReadOnlyList<TSource> sources,
        int maxConcurrency,
        Func<TSource, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(maxConcurrency);
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await action(source);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static AzureDevOpsWorkItemActivityMetadata GetDailyMetadata(
        IDictionary<DateOnly, AzureDevOpsWorkItemActivityMetadata> daily,
        DateTimeOffset occurredAt,
        string organizationUrl,
        AzureDevOpsWorkItemScope scope,
        AzureDevOpsWorkItemMetadata workItem)
    {
        var date = DateOnly.FromDateTime(occurredAt.LocalDateTime);
        if (!daily.TryGetValue(date, out var metadata))
        {
            metadata = new AzureDevOpsWorkItemActivityMetadata
            {
                OrganizationUrl = SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(organizationUrl),
                ProjectId = scope.ProjectId,
                ProjectName = scope.ProjectName,
                WorkItem = workItem
            };
            daily[date] = metadata;
        }
        return metadata;
    }

    private static void AddDiscussion(AzureDevOpsWorkItemActivityMetadata metadata, AzureDevOpsWorkItemDiscussion discussion)
    {
        var existingIndex = metadata.Discussions.FindIndex(item => item.Id == discussion.Id);
        if (existingIndex >= 0)
        {
            metadata.Discussions[existingIndex] = discussion;
        }
        else
        {
            metadata.Discussions.Add(discussion);
        }
    }

    private static bool IsAuditField(string field) => field is
        "System.ChangedBy" or "System.ChangedDate" or "System.RevisedDate" or "System.AuthorizedDate" or
        "System.AuthorizedAs" or "System.PersonId" or "System.Rev" or "System.Watermark" or "System.CommentCount" or "System.History";

    private sealed record WorkItemActivityCandidate(
        AzureDevOpsWorkItemReference Reference,
        IReadOnlyList<AzureDevOpsWorkItemUpdate> Updates);

    private sealed record CollectedWorkItemActivity(
        AzureDevOpsWorkItemMetadata WorkItem,
        IReadOnlyList<AzureDevOpsWorkItemUpdate> Updates,
        IReadOnlyList<AzureDevOpsWorkItemComment> Comments);

    private static bool IsPlaceholderTimestamp(DateTimeOffset value) => value.Year == 9999;

    private static string BuildWorkItemActivityMessage(AzureDevOpsWorkItemActivityMetadata metadata)
    {
        var changes = metadata.FieldChanges.Select(change => change.Field).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var discussions = metadata.Discussions.Select(discussion => discussion.Text).Where(text => !string.IsNullOrWhiteSpace(text));
        var lines = new List<string>();
        if (changes.Count > 0) lines.Add("異動欄位：" + string.Join("、", changes));
        lines.AddRange(discussions);
        return string.Join(Environment.NewLine, lines);
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

    private static bool IsCompleteWorkItemScope(AzureDevOpsWorkItemScope scope) =>
        !string.IsNullOrWhiteSpace(scope.ProjectId);

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

    private static string BuildWorkItemRepositoryKey(string organizationUrl, AzureDevOpsWorkItemScope scope)
    {
        var organization = new Uri(organizationUrl);
        var path = organization.AbsolutePath.Trim('/');
        var organizationKey = string.IsNullOrWhiteSpace(path) ? organization.Host : $"{organization.Host}/{path}";
        return $"ado:{organizationKey.ToLowerInvariant()}:{scope.ProjectId.Trim()}:work-items";
    }

    private static string BuildWorkItemScopeCheckpointKey(string organizationUrl, AzureDevOpsWorkItemScope scope) =>
        $"work-items|{SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(organizationUrl)}|{SourceSettingsSerializer.AzureDevOpsWorkItemScopeKey(scope)}";

    private static string ScopeLabel(AzureDevOpsPullRequestScope scope) =>
        $"{scope.ProjectName}/{scope.RepositoryName}/{SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch)}";

    private static string WorkItemScopeLabel(AzureDevOpsWorkItemScope scope) =>
        $"{scope.ProjectName}／Work Item";

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
