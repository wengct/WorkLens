using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SourceBackfillValidationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backfill_validates_an_enabled_unready_source_and_preserves_checkpoint(bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<WorkLensDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options;
            var source = new ActivitySource
            {
                SourceType = ActivitySourceType.WslAntigravityCli, Enabled = true,
                HealthStatus = SourceHealthStatus.Unavailable, CheckpointJson = "{\"preserved\":true}"
            };
            await using (var setup = new WorkLensDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.ActivitySources.Add(source);
                await setup.SaveChangesAsync();
            }
            var adapter = new Adapter(valid);
            var factory = new Factory(options);
            var orchestrator = new SourceOrchestrator(factory, new SourceRegistry([adapter]),
                new ReportInvalidationService(factory), NullLogger<SourceOrchestrator>.Instance);
            var progress = new List<SourceCollectionProgress>();
            var results = await orchestrator.CollectRangeAsync([source.Id], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), progress: progress.Add);
            Assert.All(progress, item => Assert.Equal(source.Id, item.SourceId));
            Assert.Equal("準備／驗證來源", progress[0].Stage);
            Assert.Equal(valid ? "已完成" : "失敗", progress[^1].Stage);
            Assert.True(progress[^1].Finished);
            Assert.Equal(valid, progress.Any(item => item.Stage == "儲存資料"));
            Assert.True(adapter.Validated);
            Assert.Equal(valid, adapter.Collected);
            Assert.Equal(valid, Assert.Single(results).Succeeded);
            if (!valid) Assert.Contains("測試目錄不存在", results[0].Error);
            await using var check = new WorkLensDbContext(options);
            var saved = await check.ActivitySources.SingleAsync();
            Assert.Equal(ActivitySourceType.WslAntigravityCli, saved.SourceType);
            Assert.Equal(source.CheckpointJson, saved.CheckpointJson);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Backfill_picker_does_not_hide_unready_enabled_sources()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Components", "Pages", "Sources.razor"));
        var razor = await File.ReadAllTextAsync(path);
        var start = razor.IndexOf("activeWorkspace == SourceWorkspace.Backfill", StringComparison.Ordinal);
        var end = razor.IndexOf("activeWorkspace == SourceWorkspace.Overview", start, StringComparison.Ordinal);
        var backfill = razor[start..end];
        Assert.DoesNotContain("source.HealthStatus == SourceHealthStatus.Ready", backfill);
        Assert.Contains("全部已啟用來源", backfill);
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }

    private sealed class Adapter(bool valid) : IActivitySourceAdapter
    {
        public string SourceType => nameof(ActivitySourceType.WslAntigravityCli);
        public SourceCapabilities Capabilities { get; } = new(SupportsHistory: true);
        public bool Validated { get; private set; }
        public bool Collected { get; private set; }
        public Task<SourceValidationResult> ValidateAsync(ActivitySource source, CancellationToken cancellationToken)
        {
            Validated = true;
            return Task.FromResult(valid ? SourceValidationResult.Valid("可用") : SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "測試目錄不存在"));
        }
        public Task<CollectionBatch> CollectAsync(CollectionRequest request, CancellationToken cancellationToken)
        {
            Collected = true;
            Assert.False(request.UpdateCheckpoint);
            return Task.FromResult(new CollectionBatch { SuccessfulRepositories = 1, CheckpointJson = "{}" });
        }
    }
}
