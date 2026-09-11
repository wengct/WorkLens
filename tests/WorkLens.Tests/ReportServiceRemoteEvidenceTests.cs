using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ReportServiceRemoteEvidenceTests
{
    [Fact]
    public async Task Generating_daily_summary_includes_remote_evidence_within_the_period()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var date = new DateOnly(2026, 9, 11);
        var occurredAt = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(8));

        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.RemoteSourceEvidence.Add(new RemoteSourceEvidence
            {
                Id = Guid.NewGuid(),
                OriginDeviceId = Guid.NewGuid(),
                OriginEntityId = Guid.NewGuid(),
                SourceId = Guid.NewGuid(),
                RepositoryKey = "remote-repository",
                ExternalKey = "remote-evidence",
                Title = "同步佐證",
                OccurredAt = occurredAt,
                FirstObservedAt = occurredAt,
                LastObservedAt = occurredAt
            });
            await db.SaveChangesAsync();
        }

        var report = await CreateService(options).GenerateDeterministicAsync(date);

        Assert.Contains("同步佐證", report.Body);
    }

    private static ReportService CreateService(DbContextOptions<WorkLensDbContext> options)
    {
        var factory = new Factory(options);
        return new ReportService(
            factory,
            new AiProviderOrchestrator(new AiProviderRegistry([]), new Sanitizer()),
            new PromptTemplateService(factory),
            NullLogger<ReportService>.Instance);
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }

    private sealed class Sanitizer : IAiContentSanitizer
    {
        public Task<AiSanitizationResult> PrepareAsync(AiReportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiSanitizerStatus(true, "test", "test"));
    }
}
