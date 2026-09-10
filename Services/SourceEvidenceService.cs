using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SourceEvidenceService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var evidence = await db.SourceEvidence.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (evidence is null)
        {
            return false;
        }

        var date = DateOnly.FromDateTime(evidence.OccurredAt.LocalDateTime);
        db.SourceEvidence.Remove(evidence);
        await ReportInvalidationService.MarkStaleInContextAsync(db, [date], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteAutomaticForDateAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var automaticEvidence = await db.SourceEvidence
            .Where(item => item.Kind != EvidenceKind.Manual)
            .ToListAsync(cancellationToken);
        var evidence = automaticEvidence
            .Where(item => DateOnly.FromDateTime(item.OccurredAt.LocalDateTime) == date)
            .ToList();
        if (evidence.Count == 0)
        {
            return 0;
        }

        db.SourceEvidence.RemoveRange(evidence);
        await ReportInvalidationService.MarkStaleInContextAsync(db, [date], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return evidence.Count;
    }
}
