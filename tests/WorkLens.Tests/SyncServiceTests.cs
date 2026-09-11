using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SyncServiceTests
{
    [Fact]
    public async Task Jsonl_sync_imports_work_entry_on_another_installation_without_duplicateing_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "worklens-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var source = await Fixture.CreateAsync();
            await using var target = await Fixture.CreateAsync();
            var sourceService = new SyncService(source.Factory, NullLogger<SyncService>.Instance);
            var targetService = new SyncService(target.Factory, NullLogger<SyncService>.Instance);
            await sourceService.ConfigureAsync(root, "電腦 A", true);
            await targetService.ConfigureAsync(root, "電腦 B", false);
            await using (var db = source.Factory.CreateDbContext())
            {
                db.WorkEntries.Add(new WorkEntry { WorkDate = new DateOnly(2026, 9, 11), Hours = 2.5, Title = "跨機器", WorkContent = "JSONL 同步" });
                db.SourceEvidence.Add(new SourceEvidence { SourceId = Guid.NewGuid(), RepositoryKey = "worklens", RepositoryPath = "D:/SideProjects/WorkLens", Environment = "local", Kind = EvidenceKind.Commit, ExternalKey = "abc123", Title = "同步佐證", CommitMessage = "JSONL evidence", OccurredAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            await sourceService.SyncAsync();
            await targetService.SyncAsync();
            await targetService.SyncAsync();
            await using var verify = target.Factory.CreateDbContext();
            var entry = Assert.Single(await verify.RemoteWorkEntries.Where(x => !x.IsDeleted).ToListAsync());
            Assert.Equal(2.5, entry.Hours);
            Assert.Equal("JSONL 同步", entry.WorkContent);
            Assert.Equal("同步佐證", Assert.Single(await verify.RemoteSourceEvidence.Where(x => !x.IsDeleted).ToListAsync()).Title);
            Assert.Single(await verify.SyncProcessedBatches.ToListAsync());
            Assert.Single(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path;
        private Fixture(string path, Factory factory) { this.path = path; Factory = factory; }
        public Factory Factory { get; }
        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), "worklens-test-" + Guid.NewGuid().ToString("N") + ".db");
            var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var factory = new Factory(options);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return new Fixture(path, factory);
        }
        public ValueTask DisposeAsync() { if (File.Exists(path)) File.Delete(path); return ValueTask.CompletedTask; }
    }
    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
        public Task<WorkLensDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}