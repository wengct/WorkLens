using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class SensitiveScanExclusionService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<IReadOnlyList<SensitiveScanExclusion>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.SensitiveScanExclusions
            .AsNoTracking()
            .OrderBy(x => x.Value)
            .ToListAsync(cancellationToken);
    }

    public async Task<SensitiveScanExclusion> SaveAsync(
        SensitiveScanExclusion exclusion,
        CancellationToken cancellationToken = default)
    {
        var value = exclusion.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("掃描排除關鍵字不可空白。", nameof(exclusion));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var duplicate = await db.SensitiveScanExclusions.AnyAsync(
            x => x.Id != exclusion.Id && x.Value == value,
            cancellationToken);
        if (duplicate)
        {
            throw new ArgumentException("掃描排除關鍵字不可重複。", nameof(exclusion));
        }

        var existing = await db.SensitiveScanExclusions.SingleOrDefaultAsync(
            x => x.Id == exclusion.Id,
            cancellationToken);
        if (existing is null)
        {
            exclusion.Value = value;
            exclusion.CreatedAt = DateTimeOffset.UtcNow;
            exclusion.UpdatedAt = exclusion.CreatedAt;
            db.SensitiveScanExclusions.Add(exclusion);
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing ?? exclusion;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.SensitiveScanExclusions.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (existing is null)
        {
            return;
        }

        db.SensitiveScanExclusions.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
