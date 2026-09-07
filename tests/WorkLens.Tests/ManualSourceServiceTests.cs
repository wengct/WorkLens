using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ManualSourceServiceTests
{
    [Fact]
    public async Task Add_saves_manual_evidence_and_marks_reports_stale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Reports.Add(new ReportDocument { Kind = ReportKind.Daily, PeriodKey = "2026-09-03" });
            await setup.SaveChangesAsync();
        }

        var factory = new Factory(options);
        var service = new ManualSourceService(factory, new ReportInvalidationService(factory));
        var projectId = Guid.NewGuid();

        var result = await service.AddAsync(
            new DateOnly(2026, 9, 3),
            "# 需求討論\n\n- 確認匯出格式",
            projectId,
            "會議決議");

        await using var verify = new WorkLensDbContext(options);
        var evidence = await verify.SourceEvidence.SingleAsync();
        Assert.Equal(EvidenceKind.Manual, evidence.Kind);
        Assert.Equal("會議決議", evidence.Title);
        Assert.Equal(projectId, evidence.ProjectId);
        Assert.Equal(result.Id, evidence.Id);
        Assert.True((await verify.Reports.SingleAsync()).IsStale);
    }

    [Fact]
    public async Task Add_rejects_empty_content()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        var service = new ManualSourceService(factory, new ReportInvalidationService(factory));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddAsync(new DateOnly(2026, 9, 3), "  "));

        Assert.Equal("參考資料內容不可空白。", exception.Message);
    }

    [Fact]
    public async Task Update_changes_only_manual_evidence_and_marks_report_stale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Reports.Add(new ReportDocument { Kind = ReportKind.Daily, PeriodKey = "2026-09-03" });
            await setup.SaveChangesAsync();
        }

        var factory = new Factory(options);
        var service = new ManualSourceService(factory, new ReportInvalidationService(factory));
        var evidence = await service.AddAsync(new DateOnly(2026, 9, 3), "原始內容");
        await using (var clearStale = new WorkLensDbContext(options))
        {
            var report = await clearStale.Reports.SingleAsync();
            report.IsStale = false;
            await clearStale.SaveChangesAsync();
        }

        var projectId = Guid.NewGuid();
        var result = await service.UpdateAsync(evidence.Id, "# 更新內容\n\n- 新決議", projectId);

        Assert.NotNull(result);
        await using var verify = new WorkLensDbContext(options);
        var updated = await verify.SourceEvidence.SingleAsync();
        Assert.Equal("更新內容", updated.Title);
        Assert.Equal("# 更新內容\n\n- 新決議", updated.CommitMessage);
        Assert.Equal(projectId, updated.ProjectId);
        Assert.True((await verify.Reports.SingleAsync()).IsStale);
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
