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
    public async Task Shareable_context_includes_synced_entries_and_evidence_without_local_sources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var date = new DateOnly(2026, 9, 11);
        var timestamp = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(new TimeOnly(10, 0))));
        var entryId = Guid.NewGuid();
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.RemoteWorkEntries.Add(new RemoteWorkEntry { OriginDeviceId = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), Id = entryId, WorkDate = date, Hours = 2, Title = "同步工作紀錄" });
            db.RemoteWorkEntries.Add(new RemoteWorkEntry { OriginDeviceId = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), Id = Guid.NewGuid(), WorkDate = date, Hours = 9, Title = "已刪除紀錄", IsDeleted = true });
            db.RemoteSourceEvidence.Add(new RemoteSourceEvidence { OriginDeviceId = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), Id = Guid.NewGuid(), SourceId = Guid.NewGuid(), Kind = EvidenceKind.Commit, Title = "同步提交內容", OccurredAt = timestamp });
            db.RemoteSourceEvidence.Add(new RemoteSourceEvidence { OriginDeviceId = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), Id = Guid.NewGuid(), SourceId = Guid.NewGuid(), Title = "已刪除佐證", OccurredAt = timestamp, IsDeleted = true });
            db.RemoteSourceEvidence.Add(new RemoteSourceEvidence { OriginDeviceId = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), Id = Guid.NewGuid(), SourceId = Guid.NewGuid(), Title = "期間外佐證", OccurredAt = timestamp.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        var context = await CreateService(options).GetShareableDailyContextAsync(date);

        Assert.Contains("同步工作紀錄", context.Input);
        Assert.Contains("同步提交內容", context.Input);
        Assert.DoesNotContain("已刪除", context.Input);
        Assert.DoesNotContain("期間外", context.Input);
        Assert.Equal(2, context.TotalHours);
        Assert.Contains(entryId, context.EntryIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generating_summary_includes_remote_evidence_within_the_period(bool weekly)
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

        var service = CreateService(options);
        var report = weekly
            ? await service.GenerateWeeklyAsync(date)
            : await service.GenerateDeterministicAsync(date);

        Assert.Contains("同步佐證", report.Body);
        if (weekly)
        {
            Assert.Equal(new DateOnly(2026, 9, 14), DateOnly.FromDateTime(report.PeriodEnd.LocalDateTime));
        }
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
