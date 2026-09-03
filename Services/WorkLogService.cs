using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class WorkLogService(
    IDbContextFactory<WorkLensDbContext> factory,
    ReportInvalidationService invalidation)
{
    public async Task<WorkEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.WorkEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkEntry>> GetForDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(x => x.WorkDate == date)
            .ToListAsync(cancellationToken);
        return entries.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<WorkEntry>> GetRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        Guid? projectId = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.WorkEntries.AsNoTracking()
            .Where(entry => entry.WorkDate >= startDate && entry.WorkDate <= endDate &&
                            (projectId == null || entry.ProjectId == projectId))
            .ToListAsync(cancellationToken);
        return entries
            .Where(entry => string.IsNullOrWhiteSpace(query) ||
                            entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            entry.WorkContent.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.WorkDate)
            .ThenBy(entry => entry.CreatedAt)
            .ToList();
    }

    public async Task<WorkEntry> AddAsync(
        DateOnly workDate,
        double hours,
        string workContent,
        Guid? projectId = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(hours) || hours <= 0 || hours > 24)
        {
            throw new ArgumentException("時數必須大於 0 且不可超過 24 小時。");
        }

        if (string.IsNullOrWhiteSpace(workContent))
        {
            throw new ArgumentException("工作內容不可空白。");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = new WorkEntry
        {
            WorkDate = workDate,
            Hours = hours,
            Title = ContentTitle.Resolve(title, workContent),
            WorkContent = workContent.Trim(),
            ProjectId = projectId
        };

        db.WorkEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await invalidation.MarkStaleAsync([workDate], cancellationToken);
        return entry;
    }

    public async Task<WorkEntry?> UpdateAsync(
        Guid id,
        DateOnly workDate,
        double hours,
        string workContent,
        Guid? projectId,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        Validate(hours, workContent);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.WorkEntries.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var oldDate = entry.WorkDate;
        entry.WorkDate = workDate;
        entry.Hours = hours;
        entry.Title = ContentTitle.Resolve(title, workContent);
        entry.WorkContent = workContent.Trim();
        entry.ProjectId = projectId;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await invalidation.MarkStaleAsync([oldDate, workDate], cancellationToken);
        return entry;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.WorkEntries.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entry is null)
        {
            return;
        }

        var workDate = entry.WorkDate;
        db.WorkEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        await invalidation.MarkStaleAsync([workDate], cancellationToken);
    }

    private static void Validate(double hours, string workContent)
    {
        if (!double.IsFinite(hours) || hours <= 0 || hours > 24)
        {
            throw new ArgumentException("時數必須大於 0 且不可超過 24 小時。");
        }

        if (string.IsNullOrWhiteSpace(workContent))
        {
            throw new ArgumentException("工作內容不可空白。");
        }
    }

    public static double CalculateHours(IEnumerable<WorkEntry> entries) =>
        entries.Sum(x => Math.Max(0, x.Hours));
}
