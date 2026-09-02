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
    int UnchangedCount = 0);

public sealed class SourceOrchestrator(
    IDbContextFactory<WorkLensDbContext> factory,
    SourceRegistry registry,
    ReportInvalidationService invalidation,
    ILogger<SourceOrchestrator> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();

    public async Task<SourceValidationResult> ValidateAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Error, "找不到資料來源。");
        }

        var result = await registry.Get(source.SourceType).ValidateAsync(source, cancellationToken);
        source.HealthStatus = source.Enabled ? result.Status : SourceHealthStatus.Disabled;
        source.LastError = result.IsValid ? null : result.Summary;
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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
            results.Add(await CollectCoreAsync(sourceId, start, end, false, cancellationToken));
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
        try
        {
            await using var readDb = await factory.CreateDbContextAsync(cancellationToken);
            var source = await readDb.ActivitySources.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == sourceId && !x.IsArchived, cancellationToken);
            if (source is null || !source.Enabled)
            {
                return new CollectionRunResult(false, 0, [], "來源未啟用。");
            }

            if (source.HealthStatus != SourceHealthStatus.Ready)
            {
                return new CollectionRunResult(false, 0, [], "來源尚未驗證，請先按「驗證」。");
            }

            var since = requestedStart ?? (source.LastSuccessAt is null
                ? DateTimeOffset.UtcNow.AddDays(-Math.Clamp(source.InitialImportDays, 1, 90))
                : source.LastSuccessAt.Value.AddMinutes(-2));
            var adapter = registry.Get(source.SourceType);

            await SetRunningAsync(sourceId, cancellationToken);
            var batch = await adapter.CollectAsync(
                new CollectionRequest(source, since, requestedEnd, updateCheckpoint, cancellationToken),
                cancellationToken);

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var trackedSource = await db.ActivitySources.SingleAsync(x => x.Id == sourceId, cancellationToken);

            var changes = await UpsertEvidenceAsync(db, batch.Evidence, cancellationToken);
            await UpdateCurrentStatusAsync(db, batch, cancellationToken);
            await ReconcileLineageAsync(db, sourceId, batch, cancellationToken);
            await ReconcileRebaseSessionsAsync(db, sourceId, batch, cancellationToken);

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
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (changes.ChangedDates.Count > 0)
            {
                await invalidation.MarkStaleAsync(changes.ChangedDates, cancellationToken);
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
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DbUpdateException)
        {
            logger.LogWarning(exception, "資料來源 {SourceId} 收集失敗", sourceId);
            await MarkErrorAsync(sourceId, exception.Message, cancellationToken);
            return new CollectionRunResult(false, 0, [], exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SetRunningAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var source = await db.ActivitySources.SingleAsync(x => x.Id == sourceId, cancellationToken);
        source.HealthStatus = SourceHealthStatus.Running;
        source.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkErrorAsync(Guid sourceId, string error, CancellationToken cancellationToken)
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
            logger.LogWarning(markException, "無法更新來源錯誤狀態 {SourceId}", sourceId);
        }
    }

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
                if (ShouldKeepExistingCodexEvidence(current, evidence))
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
                // when Windows Git and WSL Git observe the same evidence.
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

    private static bool IsMateriallyDifferent(SourceEvidence current, SourceEvidence incoming) =>
        current.SourceId != incoming.SourceId || current.ProjectId != incoming.ProjectId ||
        current.RepositoryPath != incoming.RepositoryPath || current.Environment != incoming.Environment ||
        current.Kind != incoming.Kind || current.Title != incoming.Title || current.CommitMessage != incoming.CommitMessage || current.OccurredAt != incoming.OccurredAt ||
        current.CommitHash != incoming.CommitHash || current.ParentHashes != incoming.ParentHashes ||
        current.PatchId != incoming.PatchId || current.Branch != incoming.Branch ||
        current.MetadataJson != incoming.MetadataJson ||
        current.ReachabilityStatus != incoming.ReachabilityStatus;

    private static bool ShouldKeepExistingCodexEvidence(SourceEvidence current, SourceEvidence incoming)
    {
        if (current.Kind != EvidenceKind.CodexSession ||
            incoming.Kind != EvidenceKind.CodexSession ||
            current.SourceId == incoming.SourceId)
        {
            return false;
        }

        var currentMetadata = SourceSettingsSerializer.DeserializeCodexMetadata(current.MetadataJson);
        var incomingMetadata = SourceSettingsSerializer.DeserializeCodexMetadata(incoming.MetadataJson);
        return currentMetadata is not null && incomingMetadata is not null &&
               incomingMetadata.UpdatedAt <= currentMetadata.UpdatedAt;
    }

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
