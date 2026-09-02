using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ActivityQueryService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<IReadOnlyList<SourceEvidence>> SearchAsync(
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        Guid? projectId = null,
        string? query = null,
        CommitReachabilityStatus? reachability = null,
        int take = 500,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        var start = startDate?.ToDateTime(TimeOnly.MinValue);
        var end = endDate?.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return evidence.Where(item =>
                (start is null || item.OccurredAt.LocalDateTime >= start) &&
                (end is null || item.OccurredAt.LocalDateTime < end) &&
                (projectId is null || item.ProjectId == projectId) &&
                (reachability is null || item.ReachabilityStatus == reachability) &&
                (string.IsNullOrWhiteSpace(query) ||
                 item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.CommitMessage.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.RepositoryKey.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.OccurredAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToList();
    }

    public async Task<IReadOnlyList<SourceEvidence>> GetRecentAsync(int take = 150, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        return evidence
            .OrderByDescending(x => x.OccurredAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToList();
    }

    public async Task<IReadOnlyList<SourceEvidence>> GetForRangeAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        return evidence
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end)
            .OrderBy(x => x.OccurredAt)
            .ToList();
    }

    public async Task<IReadOnlyList<SourceEvidence>> GetCurrentAsync(
        int take = 150,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.AsNoTracking().ToListAsync(cancellationToken);
        return evidence
            .Where(x => x.ReachabilityStatus == CommitReachabilityStatus.Current)
            .OrderByDescending(x => x.OccurredAt)
            .Take(Math.Clamp(take, 1, 1000))
            .ToList();
    }

    public async Task<IReadOnlyList<WorkEntry>> GetRecentWorkEntriesAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.WorkEntries.AsNoTracking().ToListAsync(cancellationToken);
        return entries
            .OrderByDescending(x => x.WorkDate)
            .ThenByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToList();
    }

}
