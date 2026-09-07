using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ReportRevisionTests
{
    [Fact]
    public async Task Saving_changes_keeps_one_previous_version_and_restore_swaps_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var id = Guid.NewGuid();
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Reports.Add(new ReportDocument { Id = id, Body = "第一版", DeterministicBody = "第一版" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        var changed = await service.UpdateBodyAsync(id, "第二版", expectedVersion: 1);
        var previous = await service.GetPreviousRevisionAsync(id);

        Assert.Equal(2, changed!.UpdateVersion);
        Assert.Equal("第一版", previous!.Body);

        var restored = await service.RestorePreviousAsync(id, changed.UpdateVersion);
        var swapped = await service.GetPreviousRevisionAsync(id);

        Assert.Equal("第一版", restored!.Body);
        Assert.True(restored.IsStale);
        Assert.Equal("第二版", swapped!.Body);
    }

    [Fact]
    public async Task Saving_with_a_stale_version_stops_without_overwriting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var id = Guid.NewGuid();
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Reports.Add(new ReportDocument { Id = id, Body = "原內容" });
            await db.SaveChangesAsync();
        }

        var service = CreateService(options);
        await service.UpdateBodyAsync(id, "其他分頁內容", expectedVersion: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateBodyAsync(id, "舊分頁內容", expectedVersion: 1));
        Assert.Equal("其他分頁內容", (await service.GetAsync(id))!.Body);
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
        public Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiSanitizerStatus(true, "test", "test"));
    }
}
