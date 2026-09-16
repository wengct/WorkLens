using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ActivityQueryServiceTests
{
    [Fact]
    public async Task Range_loads_only_matching_payloads_and_preserves_offset_and_tick_boundaries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var materialized = new EvidenceMaterialization();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection)
            .AddInterceptors(materialized).Options;
        var start = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.FromHours(8)).AddTicks(7);
        var end = start.AddDays(1);
        var expectedLocal = new SourceEvidence { ExternalKey = "start", OccurredAt = start.ToOffset(TimeSpan.FromHours(-5)) };
        var expectedRemote = new RemoteSourceEvidence { Id = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), OccurredAt = end.AddTicks(-1).ToOffset(TimeSpan.Zero) };
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.SourceEvidence.AddRange(expectedLocal,
                new SourceEvidence { ExternalKey = "before", OccurredAt = start.AddTicks(-1), MetadataJson = new string('x', 100_000) },
                new SourceEvidence { ExternalKey = "end", OccurredAt = end });
            db.RemoteSourceEvidence.AddRange(expectedRemote,
                new RemoteSourceEvidence { Id = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), OccurredAt = start.AddDays(-1), MetadataJson = new string('x', 100_000) },
                new RemoteSourceEvidence { Id = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), OccurredAt = start, IsDeleted = true });
            await db.SaveChangesAsync();
        }

        var result = await new ActivityQueryService(new Factory(options)).GetForRangeAsync(start, end);

        Assert.Equal(new[] { expectedLocal.Id, expectedRemote.Id }, result.Select(x => x.Id));
        Assert.Equal(2, materialized.Count);
        Assert.Equal(expectedLocal.OccurredAt.Offset, result[0].OccurredAt.Offset);
        Assert.Equal(expectedRemote.OccurredAt, result[1].OccurredAt);
    }

    [Fact]
    public async Task Range_returns_all_matches_across_payload_batches()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var start = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.SourceEvidence.AddRange(Enumerable.Range(0, 1001).Select(i => new SourceEvidence
            {
                ExternalKey = $"item-{i}", OccurredAt = start.AddSeconds(i)
            }));
            await db.SaveChangesAsync();
        }

        var service = new ActivityQueryService(new Factory(options));
        var result = await service.GetForRangeAsync(start, start.AddDays(1));
        Assert.Equal(1001, result.Count);
        Assert.Equal(result.OrderBy(x => x.OccurredAt).Select(x => x.Id), result.Select(x => x.Id));
        Assert.Empty(await service.GetForRangeAsync(start, start));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetForRangeAsync(start, start.AddDays(1), cancellation.Token));
    }

    private sealed class EvidenceMaterialization : IMaterializationInterceptor
    {
        public int Count { get; private set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is SourceEvidence or RemoteSourceEvidence) Count++;
            return entity;
        }
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
