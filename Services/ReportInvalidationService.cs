using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

/// <summary>Marks generated documents stale when their underlying local facts change.</summary>
public sealed class ReportInvalidationService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task MarkStaleAsync(IEnumerable<DateOnly> dates, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await MarkStaleInContextAsync(db, dates, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static async Task MarkStaleInContextAsync(WorkLensDbContext db, IEnumerable<DateOnly> dates,
        CancellationToken cancellationToken)
    {
        var distinctDates = dates.Distinct().ToArray();
        if (distinctDates.Length == 0)
        {
            return;
        }

        var dailyKeys = distinctDates.Select(date => date.ToString("yyyy-MM-dd")).ToHashSet();
        var weeklyKeys = distinctDates.Select(GetWeeklyKey).ToHashSet();
        var reports = await db.Reports
            .Where(report => (report.Kind == ReportKind.Daily && dailyKeys.Contains(report.PeriodKey)) ||
                             (report.Kind == ReportKind.Weekly && weeklyKeys.Contains(report.PeriodKey)))
            .ToListAsync(cancellationToken);
        foreach (var report in reports)
        {
            report.IsStale = true;
        }
    }

    public async Task MarkProjectReportsStaleAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var dates = await db.WorkEntries.AsNoTracking()
            .Where(entry => entry.ProjectId == projectId)
            .Select(entry => entry.WorkDate)
            .ToListAsync(cancellationToken);
        await MarkStaleAsync(dates, cancellationToken);
    }

    public static string GetWeeklyKey(DateOnly date)
    {
        var monday = date.AddDays(-(int)date.DayOfWeek + (date.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
        return $"{monday:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue)):00}";
    }
}
