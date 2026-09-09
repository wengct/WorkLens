using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Logging;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SourceCollectionLoggingTests
{
    [Fact]
    public async Task ValidateAsync_writes_validation_failure_to_the_local_log()
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
                DisplayName = "Invalid source",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Ready
            });
            await setup.SaveChangesAsync();
        }

        var logDirectory = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new LocalFileLoggerProvider(logDirectory);
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var factory = new TestDbContextFactory(options);
            var orchestrator = new SourceOrchestrator(
                factory,
                new SourceRegistry([new InvalidAdapter()]),
                new ReportInvalidationService(factory),
                loggerFactory.CreateLogger<SourceOrchestrator>());

            var result = await orchestrator.ValidateAsync(sourceId);

            Assert.False(result.IsValid);
            var logFile = Assert.Single(Directory.GetFiles(logDirectory, "worklens-*.log"));
            var content = await File.ReadAllTextAsync(logFile);
            Assert.Contains("[Warning]", content, StringComparison.Ordinal);
            Assert.Contains("資料來源 Invalid source", content, StringComparison.Ordinal);
            Assert.Contains("驗證失敗", content, StringComparison.Ordinal);
            Assert.Contains("測試驗證失敗", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecoverStaleCollectionsAsync_recovers_only_expired_running_sources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var staleId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.ActivitySources.AddRange(
                new ActivitySource
                {
                    Id = staleId,
                    DisplayName = "Stale source",
                    SourceType = ActivitySourceType.WindowsCodex,
                    Enabled = true,
                    HealthStatus = SourceHealthStatus.Running,
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
                },
                new ActivitySource
                {
                    Id = activeId,
                    DisplayName = "Active source",
                    SourceType = ActivitySourceType.WindowsCodex,
                    Enabled = true,
                    HealthStatus = SourceHealthStatus.Running,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            await setup.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(options);
        var orchestrator = new SourceOrchestrator(
            factory,
            new SourceRegistry([new SuccessfulAdapter()]),
            new ReportInvalidationService(factory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SourceOrchestrator>.Instance);

        Assert.Equal(1, await orchestrator.RecoverStaleCollectionsAsync(TimeSpan.FromMinutes(2)));

        await using var verify = new WorkLensDbContext(options);
        Assert.Equal(SourceHealthStatus.Ready, (await verify.ActivitySources.SingleAsync(source => source.Id == staleId)).HealthStatus);
        Assert.Equal(SourceHealthStatus.Running, (await verify.ActivitySources.SingleAsync(source => source.Id == activeId)).HealthStatus);
    }

    [Fact]
    public async Task Hosted_service_startup_recovers_a_stale_running_source()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            var databasePath = Path.Combine(databaseDirectory, "worklens.db");
            var options = new DbContextOptionsBuilder<WorkLensDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var sourceId = Guid.NewGuid();
            await using (var setup = new WorkLensDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.ActivitySources.Add(new ActivitySource
                {
                    Id = sourceId,
                    DisplayName = "Stale Azure DevOps",
                    SourceType = ActivitySourceType.WindowsCodex,
                    Enabled = true,
                    HealthStatus = SourceHealthStatus.Running,
                    LastSuccessAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
                });
                await setup.SaveChangesAsync();
            }

            var factory = new TestDbContextFactory(options);
            var orchestrator = new SourceOrchestrator(
                factory,
                new SourceRegistry([new SuccessfulAdapter()]),
                new ReportInvalidationService(factory),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SourceOrchestrator>.Instance);
            var service = new SourceCollectionHostedService(
                factory,
                orchestrator,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SourceCollectionHostedService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                var recovered = await WaitUntilAsync(async () =>
                {
                    await using var verify = new WorkLensDbContext(options);
                    return (await verify.ActivitySources.SingleAsync(item => item.Id == sourceId)).HealthStatus == SourceHealthStatus.Ready;
                }, TimeSpan.FromSeconds(1));

                Assert.True(recovered);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CollectAsync_removes_existing_placeholder_Work_Item_evidence()
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
                DisplayName = "Azure DevOps",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Ready
            });
            setup.SourceEvidence.Add(new SourceEvidence
            {
                SourceId = sourceId,
                RepositoryKey = "ado:example:work-items",
                ExternalKey = "ado:example:work-items:work-item:1:9999-01-01",
                Kind = EvidenceKind.AzureDevOpsWorkItemActivity,
                Title = "Invalid placeholder",
                OccurredAt = DateTimeOffset.MaxValue
            });
            await setup.SaveChangesAsync();
        }

        var factory = new TestDbContextFactory(options);
        var orchestrator = new SourceOrchestrator(
            factory,
            new SourceRegistry([new SuccessfulAdapter()]),
            new ReportInvalidationService(factory),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SourceOrchestrator>.Instance);

        await orchestrator.CollectAsync(sourceId);

        await using var verify = new WorkLensDbContext(options);
        Assert.Empty(await verify.SourceEvidence.ToListAsync());
    }

    [Fact]
    public async Task Cancelled_collection_log_includes_the_source_display_name_and_identifier()
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
                DisplayName = "取消測試來源",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Ready
            });
            await setup.SaveChangesAsync();
        }

        var logDirectory = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new LocalFileLoggerProvider(logDirectory);
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var factory = new TestDbContextFactory(options);
            var adapter = new BlockingAdapter();
            var orchestrator = new SourceOrchestrator(
                factory,
                new SourceRegistry([adapter]),
                new ReportInvalidationService(factory),
                loggerFactory.CreateLogger<SourceOrchestrator>());

            var collection = orchestrator.CollectAsync(sourceId);
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await orchestrator.StopCollectionAsync(sourceId));
            await collection.WaitAsync(TimeSpan.FromSeconds(5));

            var logFile = Assert.Single(Directory.GetFiles(logDirectory, "worklens-*.log"));
            var content = await File.ReadAllTextAsync(logFile);
            Assert.Contains("取消測試來源", content, StringComparison.Ordinal);
            Assert.Contains(sourceId.ToString(), content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CollectAsync_writes_failed_synchronization_details_to_the_local_log()
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
                DisplayName = "Failing source",
                SourceType = ActivitySourceType.WindowsCodex,
                Enabled = true,
                HealthStatus = SourceHealthStatus.Ready
            });
            await setup.SaveChangesAsync();
        }

        var logDirectory = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new LocalFileLoggerProvider(logDirectory);
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var factory = new TestDbContextFactory(options);
            var orchestrator = new SourceOrchestrator(
                factory,
                new SourceRegistry([new FailingAdapter()]),
                new ReportInvalidationService(factory),
                loggerFactory.CreateLogger<SourceOrchestrator>());

            var result = await orchestrator.CollectAsync(sourceId);

            Assert.False(result.Succeeded);
            var logFile = Assert.Single(Directory.GetFiles(logDirectory, "worklens-*.log"));
            var content = await File.ReadAllTextAsync(logFile);
            Assert.Contains("[Error]", content, StringComparison.Ordinal);
            Assert.Contains("資料來源同步失敗", content, StringComparison.Ordinal);
            Assert.Contains("Azure Boards 查詢失敗", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    private sealed class FailingAdapter : IActivitySourceAdapter
    {
        public string SourceType => ActivitySourceType.WindowsCodex.ToString();
        public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

        public Task<SourceValidationResult> ValidateAsync(ActivitySource source, CancellationToken cancellationToken) =>
            Task.FromResult(SourceValidationResult.Valid("OK"));

        public Task<CollectionBatch> CollectAsync(CollectionRequest request, CancellationToken cancellationToken)
        {
            var batch = new CollectionBatch();
            batch.Warnings.Add("Azure Boards 查詢失敗");
            return Task.FromResult(batch);
        }
    }

    private sealed class InvalidAdapter : IActivitySourceAdapter
    {
        public string SourceType => ActivitySourceType.WindowsCodex.ToString();
        public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

        public Task<SourceValidationResult> ValidateAsync(ActivitySource source, CancellationToken cancellationToken) =>
            Task.FromResult(SourceValidationResult.Invalid(SourceHealthStatus.Error, "測試驗證失敗"));

        public Task<CollectionBatch> CollectAsync(CollectionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectionBatch());
    }

    private sealed class BlockingAdapter : IActivitySourceAdapter
    {
        public string SourceType => ActivitySourceType.WindowsCodex.ToString();
        public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SourceValidationResult> ValidateAsync(ActivitySource source, CancellationToken cancellationToken) =>
            Task.FromResult(SourceValidationResult.Valid("OK"));

        public async Task<CollectionBatch> CollectAsync(CollectionRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CollectionBatch();
        }
    }

    private sealed class SuccessfulAdapter : IActivitySourceAdapter
    {
        public string SourceType => ActivitySourceType.WindowsCodex.ToString();
        public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);

        public Task<SourceValidationResult> ValidateAsync(ActivitySource source, CancellationToken cancellationToken) =>
            Task.FromResult(SourceValidationResult.Valid("OK"));

        public Task<CollectionBatch> CollectAsync(CollectionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectionBatch { SuccessfulRepositories = 1 });
    }

    private sealed class TestDbContextFactory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);

        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return await condition();
    }
}
