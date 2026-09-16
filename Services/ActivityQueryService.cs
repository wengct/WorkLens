using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ActivityQueryService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<IReadOnlyList<SourceEvidence>> SearchAsync(DateOnly? startDate = null, DateOnly? endDate = null, Guid? projectId = null, string? query = null, CommitReachabilityStatus? reachability = null, int take = 500, CancellationToken cancellationToken = default)
    {
        var evidence = await GetAllEvidenceAsync(cancellationToken);
        var start = startDate?.ToDateTime(TimeOnly.MinValue);
        var end = endDate?.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return evidence.Where(item => (start is null || item.OccurredAt.LocalDateTime >= start) && (end is null || item.OccurredAt.LocalDateTime < end) && (projectId is null || item.ProjectId == projectId) && (reachability is null || item.ReachabilityStatus == reachability) && (string.IsNullOrWhiteSpace(query) || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.CommitMessage.Contains(query, StringComparison.OrdinalIgnoreCase) || item.RepositoryKey.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderByDescending(item => item.OccurredAt).Take(Math.Clamp(take, 1, 1000)).ToList();
    }
    public async Task<IReadOnlyList<SourceEvidence>> GetRecentAsync(int take = 150, CancellationToken cancellationToken = default) => (await GetAllEvidenceAsync(cancellationToken)).OrderByDescending(x => x.OccurredAt).Take(Math.Clamp(take, 1, 1000)).ToList();
    public async Task<IReadOnlyList<SourceEvidence>> GetForRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = new List<SourceEvidence>();
        var local = db.SourceEvidence.AsNoTracking();
        var remote = db.RemoteSourceEvidence.AsNoTracking().Where(x => !x.IsDeleted);
        // SQLite cannot translate DateTimeOffset range comparisons. Stream only
        // identity and timestamp, then fetch payloads for the matching rows.
        var localIds = await FindIdsAsync(local.Select(x => new EvidenceTime(x.Id, x.OccurredAt)));
        foreach (var ids in localIds.Chunk(500))
        {
            evidence.AddRange(await local.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken));
        }
        var remoteIds = await FindIdsAsync(remote.Select(x => new EvidenceTime(x.Id, x.OccurredAt)));
        foreach (var ids in remoteIds.Chunk(500))
        {
            evidence.AddRange((await remote.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken)).Select(ToEvidence));
        }
        return evidence.Where(x => x.OccurredAt >= start && x.OccurredAt < end).OrderBy(x => x.OccurredAt).ToList();

        async Task<List<Guid>> FindIdsAsync(IQueryable<EvidenceTime> query)
        {
            var ids = new List<Guid>();
            await foreach (var item in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                if (item.OccurredAt >= start && item.OccurredAt < end) ids.Add(item.Id);
            }
            return ids;
        }
    }

    private sealed record EvidenceTime(Guid Id, DateTimeOffset OccurredAt);
    public async Task<IReadOnlyList<SourceEvidence>> GetCurrentAsync(int take = 150, CancellationToken cancellationToken = default) => (await GetAllEvidenceAsync(cancellationToken)).Where(x => x.ReachabilityStatus == CommitReachabilityStatus.Current).OrderByDescending(x => x.OccurredAt).Take(Math.Clamp(take, 1, 1000)).ToList();
    public async Task<IReadOnlyList<WorkEntry>> GetRecentWorkEntriesAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var local = await db.WorkEntries.AsNoTracking().ToListAsync(cancellationToken);
        var remote = await db.RemoteWorkEntries.AsNoTracking().Where(x => !x.IsDeleted).ToListAsync(cancellationToken);
        return local.Concat(remote.Select(ToWorkEntry)).OrderByDescending(x => x.WorkDate).ThenByDescending(x => x.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToList();
    }
    private async Task<List<SourceEvidence>> GetAllEvidenceAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var local = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        var remote = await db.RemoteSourceEvidence.AsNoTracking().Where(x => !x.IsDeleted).ToListAsync(cancellationToken);
        return local.Concat(remote.Select(ToEvidence)).ToList();
    }
    internal static WorkEntry ToWorkEntry(RemoteWorkEntry value) => new() { Id = value.Id, WorkDate = value.WorkDate, Hours = value.Hours, Title = value.Title, WorkContent = value.WorkContent, CreatedAt = value.CreatedAt, UpdatedAt = value.UpdatedAt };
    internal static SourceEvidence ToEvidence(RemoteSourceEvidence value) => new() { Id = value.Id, SourceId = value.SourceId, RepositoryKey = value.RepositoryKey, RepositoryPath = value.RepositoryPath, Environment = value.Environment, Kind = value.Kind, ExternalKey = value.ExternalKey, Title = value.Title, CommitMessage = value.CommitMessage, OccurredAt = value.OccurredAt, CommitHash = value.CommitHash, ParentHashes = value.ParentHashes, PatchId = value.PatchId, Branch = value.Branch, MetadataJson = value.MetadataJson, ReachabilityStatus = value.ReachabilityStatus, FirstObservedAt = value.FirstObservedAt, LastObservedAt = value.LastObservedAt };
}
