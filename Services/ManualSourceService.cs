using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class ManualSourceService(
    IDbContextFactory<WorkLensDbContext> factory,
    ReportInvalidationService invalidation)
{
    private static readonly Guid ManualSourceId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private const string RepositoryKey = "manual";

    public async Task<SourceEvidence> AddAsync(
        DateOnly date,
        string content,
        Guid? projectId = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("人工來源內容不可空白。");
        }

        var normalizedContent = content.Trim();
        var id = Guid.NewGuid();
        var localNow = DateTime.Now;
        var localOccurredAt = date.ToDateTime(TimeOnly.FromDateTime(localNow));
        var evidence = new SourceEvidence
        {
            Id = id,
            SourceId = ManualSourceId,
            ProjectId = projectId,
            RepositoryKey = RepositoryKey,
            Environment = "Manual",
            Kind = EvidenceKind.Manual,
            ExternalKey = $"manual:{id:N}",
            Title = ContentTitle.Resolve(title, normalizedContent, "人工來源"),
            CommitMessage = normalizedContent,
            OccurredAt = new DateTimeOffset(localOccurredAt, TimeZoneInfo.Local.GetUtcOffset(localOccurredAt)),
            ReachabilityStatus = CommitReachabilityStatus.Unknown
        };

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.SourceEvidence.Add(evidence);
        await db.SaveChangesAsync(cancellationToken);
        await invalidation.MarkStaleAsync([date], cancellationToken);
        return evidence;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.SingleOrDefaultAsync(
            item => item.Id == id && item.Kind == EvidenceKind.Manual,
            cancellationToken);
        if (evidence is null)
        {
            return;
        }

        var date = DateOnly.FromDateTime(evidence.OccurredAt.LocalDateTime);
        db.SourceEvidence.Remove(evidence);
        await db.SaveChangesAsync(cancellationToken);
        await invalidation.MarkStaleAsync([date], cancellationToken);
    }

    public async Task<SourceEvidence?> UpdateAsync(
        Guid id,
        string content,
        Guid? projectId,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("人工來源內容不可空白。");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.SingleOrDefaultAsync(
            item => item.Id == id && item.Kind == EvidenceKind.Manual,
            cancellationToken);
        if (evidence is null)
        {
            return null;
        }

        var normalizedContent = content.Trim();
        evidence.ProjectId = projectId;
        evidence.Title = ContentTitle.Resolve(title, normalizedContent, "人工來源");
        evidence.CommitMessage = normalizedContent;
        evidence.LastObservedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await invalidation.MarkStaleAsync(
            [DateOnly.FromDateTime(evidence.OccurredAt.LocalDateTime)],
            cancellationToken);
        return evidence;
    }

}
