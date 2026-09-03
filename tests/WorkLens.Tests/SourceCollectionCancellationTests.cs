using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SourceCollectionCancellationTests
{
    [Fact]
    public async Task StopCollection_stops_active_adapter_and_restores_ready_status()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var sourceId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ActivitySources.Add(new ActivitySource
            {
                Id = sourceId,
                DisplayName = "Cancelable source",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Ready
            });
            await setup.SaveChangesAsync();
        }

        var factory = new Factory(options);
        var adapter = new BlockingAdapter();
        var orchestrator = new SourceOrchestrator(
            factory,
            new SourceRegistry([adapter]),
            new ReportInvalidationService(factory),
            NullLogger<SourceOrchestrator>.Instance);

        var collection = orchestrator.CollectAsync(sourceId);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await orchestrator.StopCollectionAsync(sourceId));
        var result = await collection.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Canceled);
        Assert.False(result.Succeeded);
        await using var verify = new WorkLensDbContext(options);
        var source = await verify.ActivitySources.SingleAsync(item => item.Id == sourceId);
        Assert.Equal(SourceHealthStatus.Ready, source.HealthStatus);
        Assert.Null(source.LastError);
        Assert.Empty(await verify.SourceEvidence.ToListAsync());
    }

    [Fact]
    public async Task StopCollection_accepts_a_stale_running_source_after_process_restart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var sourceId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ActivitySources.Add(new ActivitySource
            {
                Id = sourceId,
                DisplayName = "Stale running source",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Running
            });
            await setup.SaveChangesAsync();
        }

        var factory = new Factory(options);
        var orchestrator = new SourceOrchestrator(
            factory,
            new SourceRegistry([new BlockingAdapter()]),
            new ReportInvalidationService(factory),
            NullLogger<SourceOrchestrator>.Instance);

        Assert.True(await orchestrator.StopCollectionAsync(sourceId));

        await using var verify = new WorkLensDbContext(options);
        Assert.Equal(
            SourceHealthStatus.Ready,
            (await verify.ActivitySources.SingleAsync(item => item.Id == sourceId)).HealthStatus);
    }

    private sealed class BlockingAdapter : IActivitySourceAdapter
    {
        public string SourceType => ActivitySourceType.WindowsCodex.ToString();
        public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SourceValidationResult> ValidateAsync(
            ActivitySource source,
            CancellationToken cancellationToken) =>
            Task.FromResult(SourceValidationResult.Valid("OK"));

        public async Task<CollectionBatch> CollectAsync(
            CollectionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CollectionBatch();
        }
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
