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
        CancellationToken cancellationToken = default,
        WorkDraftCommit? draftCommit = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("參考資料內容不可空白。");
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
            Title = ContentTitle.Resolve(title, normalizedContent, "參考資料"),
            CommitMessage = normalizedContent,
            OccurredAt = new DateTimeOffset(localOccurredAt, TimeZoneInfo.Local.GetUtcOffset(localOccurredAt)),
            ReachabilityStatus = CommitReachabilityStatus.Unknown
        };

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await WorkDraftService.ConsumeAsync(db, draftCommit, WorkDraftKind.ManualCreate, null, cancellationToken);
        db.SourceEvidence.Add(evidence);
        await ReportInvalidationService.MarkStaleInContextAsync(db, [date], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
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
        CancellationToken cancellationToken = default,
        WorkDraftCommit? draftCommit = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("參考資料內容不可空白。");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.SingleOrDefaultAsync(
            item => item.Id == id && item.Kind == EvidenceKind.Manual,
            cancellationToken);
        if (evidence is null)
        {
            if (draftCommit is not null) throw new InvalidOperationException("原紀錄已刪除，草稿仍保留。請先複製草稿內容。");
            return null;
        }

        await WorkDraftService.ConsumeAsync(db, draftCommit, WorkDraftKind.ManualEdit, id, cancellationToken);
        var normalizedContent = content.Trim();
        evidence.ProjectId = projectId;
        evidence.Title = ContentTitle.Resolve(title, normalizedContent, "參考資料");
        evidence.CommitMessage = normalizedContent;
        evidence.LastObservedAt = DateTimeOffset.UtcNow;
        await ReportInvalidationService.MarkStaleInContextAsync(db,
            [DateOnly.FromDateTime(evidence.OccurredAt.LocalDateTime)], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return evidence;
    }

}
