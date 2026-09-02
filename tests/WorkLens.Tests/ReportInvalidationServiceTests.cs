using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ReportInvalidationServiceTests
{
    [Fact]
    public async Task Marks_only_the_affected_daily_and_weekly_reports_stale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Reports.AddRange(
                new ReportDocument { Kind = ReportKind.Daily, PeriodKey = "2026-09-02" },
                new ReportDocument { Kind = ReportKind.Weekly, PeriodKey = "2026-W36" },
                new ReportDocument { Kind = ReportKind.Daily, PeriodKey = "2026-09-15" });
            await db.SaveChangesAsync();
        }

        var service = new ReportInvalidationService(new Factory(options));
        await service.MarkStaleAsync([new DateOnly(2026, 9, 2)]);

        await using var verify = new WorkLensDbContext(options);
        var reports = await verify.Reports.OrderBy(report => report.PeriodKey).ToListAsync();
        Assert.False(reports.Single(report => report.PeriodKey == "2026-09-15").IsStale);
        Assert.True(reports.Single(report => report.PeriodKey == "2026-09-02").IsStale);
        Assert.True(reports.Single(report => report.PeriodKey == "2026-W36").IsStale);
    }

    [Fact]
    public void Period_override_takes_priority_over_general_prompt()
    {
        var configuration = new AiProviderConfiguration
        {
            GeneralReportPrompt = "通用",
            DailyReportPromptOverride = "每日"
        };

        Assert.Equal("每日", ReportService.ResolvePrompt(configuration, ReportKind.Daily));
        Assert.Equal("通用", ReportService.ResolvePrompt(configuration, ReportKind.Weekly));
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
