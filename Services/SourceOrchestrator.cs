using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed record CollectionRunResult(
    bool Succeeded,
    int EvidenceCount,
    IReadOnlyList<string> Warnings,
    string? Error = null,
    int AddedCount = 0,
    int UpdatedCount = 0,
    int UnchangedCount = 0,
    bool Canceled = false);

public sealed class SourceOrchestrator(
    IDbContextFactory<WorkLensDbContext> factory,
    SourceRegistry registry,
    ReportInvalidationService invalidation,
    ILogger<SourceOrchestrator> logger)
{
    private static readonly TimeSpan CollectionHeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeCollections = new();

    public async Task<int> RecoverStaleCollectionsAsync(
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        if (staleAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        var staleBefore = DateTimeOffset.UtcNow - staleAfter;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var staleSources = await db.ActivitySources.AsNoTracking()
            .Where(source => source.HealthStatus == SourceHealthStatus.Running &&
                             !source.IsArchived)
            .ToListAsync(cancellationToken);
        staleSources = staleSources
            .Where(source => source.UpdatedAt <= staleBefore)
            .ToList();
        var recovered = 0;
        foreach (var source in staleSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await StopCollectionAsync(source.Id, cancellationToken))
            {
                recovered++;
                logger.LogWarning(
                    "資料來源 {Source} 已超過 {StaleAfter} 未回報收集心跳，已自動復原。",
                    SourceLogLabel(source),
                    staleAfter);
            }
        }

        return recovered;
    }

    public async Task<bool> StopCollectionAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        if (activeCollections.TryGetValue(sourceId, out var cancellation))
        {
            try
            {
                cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The collection completed between lookup and cancellation. Check the durable
                // status below in case this is an interrupted run from a previous process.
            }
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(
            item => item.Id == sourceId && !item.IsArchived,
            cancellationToken);
        if (source?.HealthStatus != SourceHealthStatus.Running)
        {
            return false;
        }

        source.HealthStatus = source.Enabled ? SourceHealthStatus.Ready : SourceHealthStatus.Disabled;
        source.LastError = null;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("已復原沒有活動收集工作的 Running 來源 {Source}", SourceLogLabel(source));
        return true;
    }

    public async Task<SourceValidationResult> ValidateAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
        if (source is null)
        {
            logger.LogWarning("資料來源驗證失敗：找不到來源 {SourceId}", sourceId);
            return SourceValidationResult.Invalid(SourceHealthStatus.Error, "找不到資料來源。");
        }

        SourceValidationResult result;
        try
        {
            result = await registry.Get(source.SourceType).ValidateAsync(source, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "資料來源 {Source} 驗證發生未預期錯誤", SourceLogLabel(source));
            throw;
        }

        source.HealthStatus = source.Enabled ? result.Status : SourceHealthStatus.Disabled;
        source.LastError = result.IsValid ? null : result.Summary;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (!result.IsValid)
        {
            logger.LogWarning(
                "資料來源 {Source} 驗證失敗，狀態：{Status}，原因：{Summary}",
                SourceLogLabel(source),
                result.Status,
                result.Summary);
        }

        return result;
    }

    public async Task<CollectionRunResult> CollectAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
        => await CollectCoreAsync(sourceId, null, null, true, cancellationToken);

    public async Task<IReadOnlyList<CollectionRunResult>> CollectRangeAsync(
        IEnumerable<Guid> sourceIds,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate || endDate.DayNumber - startDate.DayNumber > 89)
        {
            throw new ArgumentException("回補日期範圍必須介於 1 到 90 天。", nameof(endDate));
        }

        var start = new DateTimeOffset(DateTime.SpecifyKind(startDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        var end = new DateTimeOffset(DateTime.SpecifyKind(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local));
        var results = new List<CollectionRunResult>();
        foreach (var sourceId in sourceIds.Distinct())
        {
            var result = await CollectCoreAsync(sourceId, start, end, false, cancellationToken);
            results.Add(result);
            if (result.Canceled)
            {
                break;
            }
        }
        return results;
    }

    private async Task<CollectionRunResult> CollectCoreAsync(
        Guid sourceId,
        DateTimeOffset? requestedStart,
        DateTimeOffset? requestedEnd,
        bool updateCheckpoint,
        CancellationToken cancellationToken)
    {
        var gate = locks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        using var collectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(collectionCancellation.Token);
        activeCollections[sourceId] = collectionCancellation;
        var collectionToken = collectionCancellation.Token;
        var sourceLabel = sourceId.ToString();
        Task? heartbeatTask = null;
        try
        {
            await using var readDb = await factory.CreateDbContextAsync(collectionToken);
            var source = await readDb.ActivitySources.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == sourceId && !x.IsArchived, collectionToken);
            if (source is null || !source.Enabled)
            {
                return new CollectionRunResult(false, 0, [], "來源未啟用。");
            }

            sourceLabel = SourceLogLabel(source);

            if (source.HealthStatus != SourceHealthStatus.Ready)
            {
                return new CollectionRunResult(false, 0, [], "來源尚未就緒，請按「重新驗證」確認來源可用。");
            }

            var since = requestedStart ?? (source.LastSuccessAt is null
                ? DateTimeOffset.UtcNow.AddDays(-Math.Clamp(source.InitialImportDays, 1, 90))
                : source.LastSuccessAt.Value.AddMinutes(-2));
            var adapter = registry.Get(source.SourceType);

            await SetRunningAsync(sourceId, collectionToken);
            heartbeatTask = KeepCollectionHeartbeatAsync(sourceId, heartbeatCancellation.Token);
            var batch = await adapter.CollectAsync(
                new CollectionRequest(source, since, requestedEnd, updateCheckpoint, collectionToken),
                collectionToken);
            collectionToken.ThrowIfCancellationRequested();
            heartbeatCancellation.Cancel();
            await WaitForHeartbeatAsync(heartbeatTask);
            heartbeatTask = null;

            await using var db = await factory.CreateDbContextAsync(collectionToken);
            await using var transaction = await db.Database.BeginTransactionAsync(collectionToken);
            var trackedSource = await db.ActivitySources.SingleAsync(x => x.Id == sourceId, collectionToken);

            await RemovePlaceholderWorkItemEvidenceAsync(db, sourceId, collectionToken);
            var changes = await UpsertEvidenceAsync(db, batch.Evidence, collectionToken);
            await UpdateCurrentStatusAsync(db, batch, collectionToken);
            await ReconcileLineageAsync(db, sourceId, batch, collectionToken);
            await ReconcileRebaseSessionsAsync(db, sourceId, batch, collectionToken);

            if (updateCheckpoint)
            {
                trackedSource.CheckpointJson = batch.CheckpointJson;
                trackedSource.LastSuccessAt = DateTimeOffset.UtcNow;
            }
            trackedSource.HealthStatus = batch.SuccessfulRepositories > 0
                ? SourceHealthStatus.Ready
                : SourceHealthStatus.Error;
            trackedSource.LastError = batch.Warnings.Count == 0
                ? null
                : string.Join(Environment.NewLine, batch.Warnings.Take(10));
            trackedSource.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(collectionToken);
            await transaction.CommitAsync(collectionToken);

            if (batch.SuccessfulRepositories == 0 && batch.Warnings.Count > 0)
            {
                logger.LogError(
                    "資料來源同步失敗 {Source}：{Errors}",
                    sourceLabel,
                    string.Join(Environment.NewLine, batch.Warnings.Take(10)));
            }
            else if (batch.Warnings.Count > 0)
            {
                logger.LogWarning(
                    "資料來源同步完成，但 {Source} 有 {WarningCount} 個警告：{Warnings}",
                    sourceLabel,
                    batch.Warnings.Count,
                    string.Join(Environment.NewLine, batch.Warnings.Take(10)));
            }

            if (changes.ChangedDates.Count > 0)
            {
                // The collection transaction is already committed. Finish its bookkeeping even if
                // cancellation arrives during this final, non-collecting step.
                await invalidation.MarkStaleAsync(changes.ChangedDates, CancellationToken.None);
            }

            return new CollectionRunResult(
                batch.SuccessfulRepositories > 0,
                batch.Evidence.Count,
                batch.Warnings,
                batch.SuccessfulRepositories == 0 ? trackedSource.LastError : null,
                changes.Added,
                changes.Updated,
                changes.Unchanged);
        }
        catch (OperationCanceledException) when (collectionToken.IsCancellationRequested)
        {
            logger.LogInformation("資料來源 {Source} 收集已由使用者或主機取消", sourceLabel);
            await RestoreReadyAfterCancellationAsync(sourceId, sourceLabel);
            return new CollectionRunResult(false, 0, [], "收集已停止。", Canceled: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DbUpdateException)
        {
            logger.LogWarning(exception, "資料來源 {Source} 收集失敗", sourceLabel);
            await MarkErrorAsync(sourceId, sourceLabel, exception.Message, cancellationToken);
            return new CollectionRunResult(false, 0, [], exception.Message);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            if (heartbeatTask is not null)
            {
                await WaitForHeartbeatAsync(heartbeatTask);
            }
            activeCollections.TryRemove(sourceId, out _);
            gate.Release();
        }
    }

    private async Task RestoreReadyAfterCancellationAsync(Guid sourceId, string sourceLabel)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == sourceId, CancellationToken.None);
            if (source is null)
            {
                return;
            }

            source.HealthStatus = source.Enabled ? SourceHealthStatus.Ready : SourceHealthStatus.Disabled;
            source.LastError = null;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning(exception, "無法還原已取消來源 {Source} 的狀態", sourceLabel);
        }
    }

    private async Task SetRunningAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleAsync(x => x.Id == sourceId, cancellationToken);
        source.HealthStatus = SourceHealthStatus.Running;
        source.LastError = null;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task KeepCollectionHeartbeatAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CollectionHeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                var source = await db.ActivitySources.SingleOrDefaultAsync(
                    item => item.Id == sourceId && item.HealthStatus == SourceHealthStatus.Running,
                    cancellationToken);
                if (source is null)
                {
                    return;
                }

                source.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DbUpdateException)
        {
            logger.LogWarning(exception, "無法更新資料來源 {SourceId} 的收集心跳", sourceId);
        }
    }

    private static async Task WaitForHeartbeatAsync(Task heartbeatTask)
    {
        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MarkErrorAsync(Guid sourceId, string sourceLabel, string error, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
            if (source is null)
            {
                return;
            }

            source.HealthStatus = SourceHealthStatus.Error;
            source.LastError = error;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception markException) when (markException is InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning(markException, "無法更新來源 {Source} 的錯誤狀態", sourceLabel);
        }
    }

    private static string SourceLogLabel(ActivitySource source) =>
        string.IsNullOrWhiteSpace(source.DisplayName)
            ? source.Id.ToString()
            : $"{source.DisplayName} ({source.Id})";

    private static async Task<EvidenceUpsertResult> UpsertEvidenceAsync(
        WorkLensDbContext db,
        IEnumerable<SourceEvidence> incoming,
        CancellationToken cancellationToken)
    {
        var result = new EvidenceUpsertResult();
        var distinct = incoming
            .GroupBy(x => new { x.RepositoryKey, x.ExternalKey })
            .Select(x => x.Last())
            .ToList();
        var keys = distinct.Select(x => x.ExternalKey).Distinct().ToArray();
        var existing = await db.SourceEvidence
            .Where(x => keys.Contains(x.ExternalKey))
            .ToListAsync(cancellationToken);
        var lookup = existing
            .GroupBy(x => (x.RepositoryKey, x.ExternalKey))
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.LastObservedAt).First());

        foreach (var evidence in distinct)
        {
            if (lookup.TryGetValue((evidence.RepositoryKey, evidence.ExternalKey), out var current))
            {
                if (ShouldKeepExistingSessionEvidence(current, evidence))
                {
                    current.LastObservedAt = DateTimeOffset.UtcNow;
                    result.Unchanged++;
                    continue;
                }

                var changed = IsMateriallyDifferent(current, evidence);
                var firstObserved = current.FirstObservedAt;
                var previousDate = DateOnly.FromDateTime(current.OccurredAt.LocalDateTime);
                // The evidence identity is repository + external key. Keep the
                // database-generated Id and original observation time stable
                // when native Git and WSL Git observe the same evidence.
                current.SourceId = evidence.SourceId;
                current.ProjectId = evidence.ProjectId;
                current.RepositoryPath = evidence.RepositoryPath;
                current.Environment = evidence.Environment;
                current.Kind = evidence.Kind;
                current.Title = evidence.Title;
                current.CommitMessage = evidence.CommitMessage;
                current.OccurredAt = evidence.OccurredAt;
                current.CommitHash = evidence.CommitHash;
                current.ParentHashes = evidence.ParentHashes;
                current.PatchId = evidence.PatchId;
                current.Branch = evidence.Branch;
                current.MetadataJson = evidence.MetadataJson;
                current.ReachabilityStatus = evidence.ReachabilityStatus;
                current.FirstObservedAt = firstObserved;
                current.LastObservedAt = DateTimeOffset.UtcNow;
                if (changed)
                {
                    result.Updated++;
                    result.ChangedDates.Add(previousDate);
                    result.ChangedDates.Add(DateOnly.FromDateTime(evidence.OccurredAt.LocalDateTime));
                }
                else
                {
                    result.Unchanged++;
                }
            }
            else
            {
                evidence.FirstObservedAt = DateTimeOffset.UtcNow;
                evidence.LastObservedAt = evidence.FirstObservedAt;
                db.SourceEvidence.Add(evidence);
                result.Added++;
                result.ChangedDates.Add(DateOnly.FromDateTime(evidence.OccurredAt.LocalDateTime));
            }
        }
        return result;
    }

    private static async Task RemovePlaceholderWorkItemEvidenceAsync(
        WorkLensDbContext db,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var placeholders = await db.SourceEvidence
            .Where(item => item.SourceId == sourceId && item.Kind == EvidenceKind.AzureDevOpsWorkItemActivity)
            .ToListAsync(cancellationToken);
        var invalid = placeholders.Where(item => item.OccurredAt.Year == 9999).ToList();
        if (invalid.Count > 0)
        {
            db.SourceEvidence.RemoveRange(invalid);
        }
    }

    private static bool IsMateriallyDifferent(SourceEvidence current, SourceEvidence incoming) =>
        current.SourceId != incoming.SourceId || current.ProjectId != incoming.ProjectId ||
        current.RepositoryPath != incoming.RepositoryPath || current.Environment != incoming.Environment ||
        current.Kind != incoming.Kind || current.Title != incoming.Title || current.CommitMessage != incoming.CommitMessage || current.OccurredAt != incoming.OccurredAt ||
        current.CommitHash != incoming.CommitHash || current.ParentHashes != incoming.ParentHashes ||
        current.PatchId != incoming.PatchId || current.Branch != incoming.Branch ||
        current.MetadataJson != incoming.MetadataJson ||
        current.ReachabilityStatus != incoming.ReachabilityStatus;

    private static bool ShouldKeepExistingSessionEvidence(SourceEvidence current, SourceEvidence incoming)
    {
        if (!IsSessionEvidence(current.Kind) ||
            current.Kind != incoming.Kind ||
            (current.SourceId == incoming.SourceId && incoming.Kind != EvidenceKind.CopilotSession))
        {
            return false;
        }

        if (IsComplete(current) && !IsComplete(incoming))
        {
            return true;
        }

        if (!IsComplete(current) && IsComplete(incoming))
        {
            return false;
        }

        var currentUpdatedAt = SessionUpdatedAt(current);
        var incomingUpdatedAt = SessionUpdatedAt(incoming);
        return currentUpdatedAt is not null && incomingUpdatedAt is not null &&
               incomingUpdatedAt <= currentUpdatedAt;
    }

    private static bool IsSessionEvidence(EvidenceKind kind) =>
        kind is EvidenceKind.CodexSession or EvidenceKind.ClaudeCodeSession or EvidenceKind.CopilotSession;

    private static DateTimeOffset? SessionUpdatedAt(SourceEvidence evidence) => evidence.Kind switch
    {
        EvidenceKind.CodexSession => SourceSettingsSerializer.DeserializeCodexMetadata(evidence.MetadataJson)?.UpdatedAt,
        EvidenceKind.ClaudeCodeSession => SourceSettingsSerializer.DeserializeClaudeCodeMetadata(evidence.MetadataJson)?.UpdatedAt,
        EvidenceKind.CopilotSession => SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)?.UpdatedAt,
        _ => null
    };

    private static bool IsComplete(SourceEvidence evidence) => evidence.Kind switch
    {
        EvidenceKind.CopilotSession => SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)?.IsComplete ?? true,
        _ => true
    };

    private sealed class EvidenceUpsertResult
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public HashSet<DateOnly> ChangedDates { get; } = [];
    }

    private static async Task UpdateCurrentStatusAsync(
        WorkLensDbContext db,
        CollectionBatch batch,
        CancellationToken cancellationToken)
    {
        foreach (var (repositoryKey, currentHashes) in batch.CurrentCommitsByRepository)
        {
            var commits = await db.SourceEvidence
                .Where(x => x.RepositoryKey == repositoryKey &&
                            x.Kind == EvidenceKind.Commit)
                .ToListAsync(cancellationToken);
            foreach (var commit in commits)
            {
                if (commit.CommitHash is not null && currentHashes.Contains(commit.CommitHash))
                {
                    commit.ReachabilityStatus = CommitReachabilityStatus.Current;
                }
                else if (commit.ReachabilityStatus == CommitReachabilityStatus.Current)
                {
                    commit.ReachabilityStatus = CommitReachabilityStatus.Unknown;
                }
            }
        }
    }

    private static async Task ReconcileLineageAsync(
        WorkLensDbContext db,
        Guid sourceId,
        CollectionBatch batch,
        CancellationToken cancellationToken)
    {
        var repositoryKeys = batch.CurrentCommitsByRepository.Keys.ToArray();
        if (repositoryKeys.Length == 0)
        {
            return;
        }

        var commits = await db.SourceEvidence
            .Where(x => x.Kind == EvidenceKind.Commit &&
                        repositoryKeys.Contains(x.RepositoryKey))
            .ToListAsync(cancellationToken);

        foreach (var newCommit in commits.Where(x =>
                     x.ReachabilityStatus == CommitReachabilityStatus.Current &&
                     !string.IsNullOrWhiteSpace(x.PatchId)))
        {
            var possibleRewrites = commits.Where(x =>
                x.Id != newCommit.Id &&
                x.RepositoryKey == newCommit.RepositoryKey &&
                x.PatchId == newCommit.PatchId &&
                x.OccurredAt <= newCommit.OccurredAt &&
                x.ReachabilityStatus != CommitReachabilityStatus.Current);

            foreach (var oldCommit in possibleRewrites)
            {
                var exists = await db.CommitLineages.AnyAsync(x =>
                    x.RepositoryKey == newCommit.RepositoryKey &&
                    x.OldCommitHash == oldCommit.CommitHash &&
                    x.NewCommitHash == newCommit.CommitHash,
                    cancellationToken);
                if (exists || oldCommit.CommitHash is null || newCommit.CommitHash is null)
                {
                    continue;
                }

                db.CommitLineages.Add(new CommitLineage
                {
                    SourceId = sourceId,
                    RepositoryKey = newCommit.RepositoryKey,
                    OldCommitHash = oldCommit.CommitHash,
                    NewCommitHash = newCommit.CommitHash,
                    Relation = CommitLineageRelation.Rebased,
                    Confidence = "patch-id",
                    DetectedAt = DateTimeOffset.UtcNow
                });
                oldCommit.ReachabilityStatus = CommitReachabilityStatus.Superseded;
            }
        }

        foreach (var candidate in batch.LineageCandidates)
        {
            var exists = await db.CommitLineages.AnyAsync(x =>
                x.RepositoryKey == candidate.RepositoryKey &&
                x.OldCommitHash == candidate.OldCommitHash &&
                x.NewCommitHash == candidate.NewCommitHash,
                cancellationToken);
            if (!exists)
            {
                db.CommitLineages.Add(new CommitLineage
                {
                    SourceId = sourceId,
                    RepositoryKey = candidate.RepositoryKey,
                    OldCommitHash = candidate.OldCommitHash,
                    NewCommitHash = candidate.NewCommitHash,
                    Relation = candidate.Relation,
                    Confidence = candidate.Confidence,
                    DetectedAt = DateTimeOffset.UtcNow
                });
            }
        }
    }

    private static async Task ReconcileRebaseSessionsAsync(
        WorkLensDbContext db,
        Guid sourceId,
        CollectionBatch batch,
        CancellationToken cancellationToken)
    {
        var rebaseEvents = batch.Evidence
            .Where(x => x.Kind is EvidenceKind.RebaseStarted or
                             EvidenceKind.RebaseFinished or
                             EvidenceKind.RebaseAborted or
                             EvidenceKind.ConflictObserved)
            .OrderBy(x => x.OccurredAt)
            .ToList();
        if (rebaseEvents.Count == 0)
        {
            return;
        }

        // DateTimeOffset range expressions are intentionally evaluated in memory for SQLite.
        var repositoryKeys = rebaseEvents
            .Select(x => x.RepositoryKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sessions = await db.RebaseSessions
            .Where(x => repositoryKeys.Contains(x.RepositoryKey))
            .ToListAsync(cancellationToken);

        foreach (var activity in rebaseEvents)
        {
            if (activity.Kind == EvidenceKind.RebaseStarted)
            {
                if (sessions.Any(x => x.ExternalKey == activity.ExternalKey))
                {
                    continue;
                }

                var session = new RebaseSession
                {
                    SourceId = sourceId,
                    RepositoryKey = activity.RepositoryKey,
                    ExternalKey = activity.ExternalKey,
                    StartedAt = activity.OccurredAt,
                    OriginalHead = activity.CommitHash,
                    Status = "InProgress"
                };
                db.RebaseSessions.Add(session);
                sessions.Add(session);
                continue;
            }

            var current = sessions
                .Where(x => x.RepositoryKey == activity.RepositoryKey &&
                            x.Status == "InProgress" &&
                            x.StartedAt <= activity.OccurredAt)
                .OrderByDescending(x => x.StartedAt)
                .FirstOrDefault();
            if (current is null)
            {
                continue;
            }

            current.Status = activity.Kind == EvidenceKind.RebaseAborted ? "Aborted" : "Finished";
            current.FinishedAt = activity.OccurredAt;
            current.NewHead = activity.CommitHash;
        }
    }
}
