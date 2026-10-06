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
    public async Task Unchanged_sync_emits_nothing_and_changes_deletions_and_restorations_keep_versions()
    {
        var root = Path.Combine(Path.GetTempPath(), "worklens-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            var service = new SyncService(fixture.Factory, NullLogger<SyncService>.Instance);
            await service.ConfigureAsync(root, "測試電腦", true);
            var id = Guid.NewGuid();
            await using (var db = fixture.Factory.CreateDbContext())
            {
                // The identity includes the kind; the same GUID may occur in both tables.
                db.WorkEntries.Add(new WorkEntry { Id = id, Title = "工作內容：中文與 emoji 🐈", WorkDate = new DateOnly(2026, 9, 17) });
                db.SourceEvidence.Add(new SourceEvidence { Id = id, ExternalKey = "evidence", Title = "佐證", OccurredAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            await service.SyncAsync();
            await service.SyncAsync();
            await using (var db = fixture.Factory.CreateDbContext())
            {
                Assert.Equal(2, await db.SyncOutboxEvents.CountAsync());
                Assert.All(await db.SyncEntityStates.ToListAsync(), x => Assert.Equal(1, x.Version));
                (await db.WorkEntries.SingleAsync()).Title = "已更新";
                db.SourceEvidence.Remove(await db.SourceEvidence.SingleAsync());
                await db.SaveChangesAsync();
            }
            await service.SyncAsync();
            await service.SyncAsync();
            await using (var db = fixture.Factory.CreateDbContext())
            {
                Assert.Equal(4, await db.SyncOutboxEvents.CountAsync());
                Assert.All(await db.SyncEntityStates.ToListAsync(), x => Assert.Equal(2, x.Version));
                Assert.True((await db.SyncEntityStates.SingleAsync(x => x.EntityKind == "SourceEvidence")).IsDeleted);
                db.SourceEvidence.Add(new SourceEvidence { Id = id, ExternalKey = "evidence", Title = "復原", OccurredAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            await service.SyncAsync();
            await service.SyncAsync();
            await using (var db = fixture.Factory.CreateDbContext())
            {
                Assert.Equal(5, await db.SyncOutboxEvents.CountAsync());
                var restored = await db.SyncEntityStates.SingleAsync(x => x.EntityKind == "SourceEvidence");
                Assert.Equal(3, restored.Version);
                Assert.False(restored.IsDeleted);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Joining_a_folder_without_a_marker_explains_how_to_create_or_join_a_space()
    {
        var root = Path.Combine(Path.GetTempPath(), "worklens-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var fixture = await Fixture.CreateAsync();
            var service = new SyncService(fixture.Factory, NullLogger<SyncService>.Instance);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConfigureAsync(root, "電腦 A", false));

            Assert.Contains("第一台電腦", error.Message, StringComparison.Ordinal);
            Assert.Contains("加入既有空間", error.Message, StringComparison.Ordinal);
            Assert.Contains(".worklens-sync-space.json", error.Message, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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

    [Fact]
    public async Task Catching_up_multiple_versions_imports_the_latest_work_and_evidence_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "worklens-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var source = await Fixture.CreateAsync();
            await using var target = await Fixture.CreateAsync();
            var sourceService = new SyncService(source.Factory, NullLogger<SyncService>.Instance);
            var targetService = new SyncService(target.Factory, NullLogger<SyncService>.Instance);
            await sourceService.ConfigureAsync(root, "source", true);
            await targetService.ConfigureAsync(root, "target", false);
            for (var version = 1; version <= 3; version++)
            {
                await using var db = source.Factory.CreateDbContext();
                if (version == 1)
                {
                    db.WorkEntries.Add(new WorkEntry { Title = "version 1", WorkDate = new DateOnly(2026, 10, 1) });
                    db.SourceEvidence.Add(new SourceEvidence { ExternalKey = "evidence", Title = "version 1" });
                }
                else
                {
                    (await db.WorkEntries.SingleAsync()).Title = $"version {version}";
                    (await db.SourceEvidence.SingleAsync()).Title = $"version {version}";
                }
                await db.SaveChangesAsync();
                await sourceService.SyncAsync();
            }
            var status = await targetService.SyncAsync();
            Assert.Null(status.LastError);
            await targetService.SyncAsync();
            await using var verify = target.Factory.CreateDbContext();
            var work = Assert.Single(await verify.RemoteWorkEntries.ToListAsync());
            var evidence = Assert.Single(await verify.RemoteSourceEvidence.ToListAsync());
            Assert.Equal(3, work.Version);
            Assert.Equal("version 3", work.Title);
            Assert.Equal(3, evidence.Version);
            Assert.Equal("version 3", evidence.Title);
            Assert.Equal(6, await verify.SyncProcessedEvents.CountAsync());
            Assert.Equal(3, await verify.SyncProcessedBatches.CountAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Invalid_batch_records_error_without_committing_partial_imports_and_can_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "worklens-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var source = await Fixture.CreateAsync();
            await using var target = await Fixture.CreateAsync();
            var sourceService = new SyncService(source.Factory, NullLogger<SyncService>.Instance);
            var targetService = new SyncService(target.Factory, NullLogger<SyncService>.Instance);
            await sourceService.ConfigureAsync(root, "source", true);
            await targetService.ConfigureAsync(root, "target", false);
            await using (var db = source.Factory.CreateDbContext())
            {
                db.WorkEntries.Add(new WorkEntry { Title = "retry", WorkDate = new DateOnly(2026, 10, 1) });
                await db.SaveChangesAsync();
            }
            await sourceService.SyncAsync();
            var validPath = Assert.Single(Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories));
            var bytes = System.Text.Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(validPath) + "invalid json\n");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var invalidPath = Path.Combine(Path.GetDirectoryName(validPath)!, $"invalid-{hash}.jsonl");
            await File.WriteAllBytesAsync(invalidPath, bytes);
            var status = await targetService.SyncAsync();
            Assert.False(string.IsNullOrWhiteSpace(status.LastError));
            Assert.Null(status.LastImportedAt);
            await using (var verify = target.Factory.CreateDbContext())
            {
                Assert.Empty(await verify.RemoteWorkEntries.ToListAsync());
                Assert.Empty(await verify.SyncProcessedEvents.ToListAsync());
                Assert.Empty(await verify.SyncProcessedBatches.ToListAsync());
                Assert.Equal(status.LastError, (await verify.SyncConfigurations.SingleAsync()).LastError);
            }
            File.Delete(invalidPath);
            status = await targetService.SyncAsync();
            Assert.Null(status.LastError);
            Assert.NotNull(status.LastImportedAt);
            await using var retried = target.Factory.CreateDbContext();
            Assert.Equal("retry", Assert.Single(await retried.RemoteWorkEntries.ToListAsync()).Title);
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
