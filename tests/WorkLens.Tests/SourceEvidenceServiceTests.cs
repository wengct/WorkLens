using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SourceEvidenceServiceTests
{
    [Fact]
    public async Task Delete_removes_the_selected_evidence_and_marks_related_reports_stale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var date = new DateOnly(2026, 9, 10);
        var evidenceId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SourceEvidence.AddRange(
                new SourceEvidence
                {
                    Id = evidenceId,
                    SourceId = Guid.NewGuid(),
                    RepositoryKey = "ado:example:work-items",
                    ExternalKey = "ado:example:work-item:85054:2026-09-10",
                    Kind = EvidenceKind.AzureDevOpsWorkItemActivity,
                    Title = "Work Item #85054",
                    OccurredAt = new DateTimeOffset(2026, 9, 10, 14, 39, 0, TimeSpan.FromHours(8))
                },
                new SourceEvidence
                {
                    Id = Guid.NewGuid(),
                    SourceId = Guid.NewGuid(),
                    RepositoryKey = "git:example",
                    ExternalKey = "commit:keep",
                    Kind = EvidenceKind.Commit,
                    Title = "保留的活動",
                    OccurredAt = new DateTimeOffset(2026, 9, 10, 15, 0, 0, TimeSpan.FromHours(8))
                });
            setup.Reports.AddRange(
                new ReportDocument { Kind = ReportKind.Daily, PeriodKey = date.ToString("yyyy-MM-dd") },
                new ReportDocument { Kind = ReportKind.Weekly, PeriodKey = ReportInvalidationService.GetWeeklyKey(date) },
                new ReportDocument { Kind = ReportKind.Daily, PeriodKey = "2026-09-09" });
            await setup.SaveChangesAsync();
        }

        var deleted = await new SourceEvidenceService(new Factory(options)).DeleteAsync(evidenceId);

        Assert.True(deleted);
        await using var verify = new WorkLensDbContext(options);
        var remaining = await verify.SourceEvidence.SingleAsync();
        Assert.Equal("commit:keep", remaining.ExternalKey);
        var reports = await verify.Reports.ToDictionaryAsync(report => (report.Kind, report.PeriodKey));
        Assert.True(reports[(ReportKind.Daily, date.ToString("yyyy-MM-dd"))].IsStale);
        Assert.True(reports[(ReportKind.Weekly, ReportInvalidationService.GetWeeklyKey(date))].IsStale);
        Assert.False(reports[(ReportKind.Daily, "2026-09-09")].IsStale);
    }

    [Fact]
    public async Task Delete_returns_false_when_evidence_does_not_exist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var deleted = await new SourceEvidenceService(new Factory(options)).DeleteAsync(Guid.NewGuid());

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAutomaticForDate_removes_only_that_dates_automatic_activity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var firstDate = new DateOnly(2026, 9, 9);
        var secondDate = new DateOnly(2026, 9, 10);
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SourceEvidence.AddRange(
                Evidence(EvidenceKind.Commit, "commit:one", firstDate),
                Evidence(EvidenceKind.AzureDevOpsWorkItemActivity, "work-item:85054", secondDate),
                Evidence(EvidenceKind.CodexSession, "codex:today", secondDate),
                Evidence(EvidenceKind.Manual, "manual:keep", secondDate));
            setup.Reports.AddRange(
                new ReportDocument { Kind = ReportKind.Daily, PeriodKey = firstDate.ToString("yyyy-MM-dd") },
                new ReportDocument { Kind = ReportKind.Daily, PeriodKey = secondDate.ToString("yyyy-MM-dd") });
            await setup.SaveChangesAsync();
        }

        var deletedCount = await new SourceEvidenceService(new Factory(options)).DeleteAutomaticForDateAsync(secondDate);

        Assert.Equal(2, deletedCount);
        await using var verify = new WorkLensDbContext(options);
        var remaining = await verify.SourceEvidence.OrderBy(item => item.ExternalKey).ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, item => item.ExternalKey == "commit:one");
        Assert.Contains(remaining, item => item.ExternalKey == "manual:keep" && item.Kind == EvidenceKind.Manual);
        var reports = await verify.Reports.ToDictionaryAsync(report => report.PeriodKey);
        Assert.False(reports[firstDate.ToString("yyyy-MM-dd")].IsStale);
        Assert.True(reports[secondDate.ToString("yyyy-MM-dd")].IsStale);
    }

    private static SourceEvidence Evidence(EvidenceKind kind, string externalKey, DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        SourceId = Guid.NewGuid(),
        RepositoryKey = "test",
        ExternalKey = externalKey,
        Kind = kind,
        Title = externalKey,
        OccurredAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(8))
    };

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
