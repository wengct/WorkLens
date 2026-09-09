using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SensitiveScanExclusionServiceTests
{
    [Fact]
    public async Task Save_trims_rejects_case_insensitive_duplicates_and_deletes_entries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = new SensitiveScanExclusionService(new Factory(options));
        var saved = await service.SaveAsync(new SensitiveScanExclusion { Value = " ExampleToken " });

        Assert.Equal("ExampleToken", saved.Value);
        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new SensitiveScanExclusion { Value = "exampletoken" }));
        Assert.Contains("不可重複", duplicate.Message, StringComparison.Ordinal);

        await service.DeleteAsync(saved.Id);
        Assert.Empty(await service.GetAllAsync());
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
