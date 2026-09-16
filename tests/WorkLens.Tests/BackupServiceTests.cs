using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task Create_releases_backup_file_before_computing_checksum()
    {
        var root = Path.Combine(Path.GetTempPath(), $"worklens-backup-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "data");
        var backupDirectory = Path.Combine(root, "backups");
        var databasePath = Path.Combine(dataDirectory, "worklens.db");

        try
        {
            Directory.CreateDirectory(dataDirectory);
            var options = new DbContextOptionsBuilder<WorkLensDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            IDbContextFactory<WorkLensDbContext> factory = new TestDbContextFactory(options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var paths = new AppPaths(databasePath, backupDirectory, Path.Combine(root, "logs"));
            var service = new BackupService(
                factory,
                paths,
                new RuntimeSettingsService(paths),
                NullLogger<BackupService>.Instance);

            var backup = await service.CreateAsync("Daily", "2026-09-04");

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup.FilePath));
            Assert.True(File.Exists(backup.FilePath + ".manifest.json"));
            await using (var exclusiveRead = File.Open(
                backup.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                Assert.True(exclusiveRead.Length > 0);
            }

            // A different kind must share the same retention limit.
            var latest = await service.CreateAsync("Weekly", "2026-W37");
            Assert.NotNull(latest);
            Assert.True(await service.VerifyAsync(latest.Id));
            Assert.False(File.Exists(backup.FilePath));
            Assert.False(File.Exists(backup.FilePath + ".manifest.json"));
            Assert.True(File.Exists(latest.FilePath + ".manifest.json"));
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(latest.Id, (await verify.BackupRecords.SingleAsync()).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
