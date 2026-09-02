using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class WorkLogServiceTests
{
    [Fact]
    public async Task Stores_user_entered_date_hours_and_markdown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = CreateService(options);
        var date = new DateOnly(2026, 9, 2);

        await service.AddAsync(date, 7.5, "# 工作內容\n\n- 完成離線編輯器");
        var entries = await service.GetForDateAsync(date);

        var entry = Assert.Single(entries);
        Assert.Equal(date, entry.WorkDate);
        Assert.Equal(7.5, entry.Hours);
        Assert.Equal("# 工作內容\n\n- 完成離線編輯器", entry.WorkContent);
        Assert.Equal(7.5, WorkLogService.CalculateHours(entries));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24.01)]
    public async Task Rejects_invalid_hours(double hours)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = CreateService(options);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddAsync(new DateOnly(2026, 9, 2), hours, "工作內容"));
    }

    [Fact]
    public async Task Rejects_empty_work_content()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = CreateService(options);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddAsync(new DateOnly(2026, 9, 2), 8, "  "));
    }

    private sealed class TestDbContextFactory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);

        public Task<WorkLensDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private static WorkLogService CreateService(DbContextOptions<WorkLensDbContext> options)
    {
        var factory = new TestDbContextFactory(options);
        return new WorkLogService(factory, new ReportInvalidationService(factory));
    }
}
