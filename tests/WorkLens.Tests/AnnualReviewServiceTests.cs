using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AnnualReviewServiceTests
{
    [Fact]
    public async Task Get_details_reports_local_and_remote_coverage_without_creating_achievements()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var date = new DateOnly(2025, 2, 3);
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.WorkEntries.Add(new WorkEntry { WorkDate = date, Title = "完成登入流程", WorkContent = "完成登入流程與測試", Hours = 3 });
            db.RemoteSourceEvidence.Add(new RemoteSourceEvidence
            {
                Id = Guid.NewGuid(), OriginDeviceId = Guid.NewGuid(), OriginEntityId = Guid.NewGuid(), OriginDeviceName = "筆電",
                SourceId = Guid.NewGuid(), RepositoryKey = "worklens", ExternalKey = "commit-1", Title = "修正同步問題",
                CommitMessage = "修正同步問題", OccurredAt = new DateTimeOffset(2025, 2, 4, 10, 0, 0, TimeSpan.Zero),
                FirstObservedAt = DateTimeOffset.UtcNow, LastObservedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var service = new AnnualReviewService(new Factory(options));
        var review = await service.CreateAsync(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), "年度回顧");
        var details = Assert.IsType<AnnualReviewDetails>(await service.GetDetailsAsync(review.Id));
        Assert.Empty(details.Achievements);
        Assert.Equal(1, details.Coverage.Sum(x => x.WorkEntryCount));
        Assert.Equal(1, details.Coverage.Sum(x => x.EvidenceCount));
    }

    [Fact]
    public async Task Generate_draft_uses_only_confirmed_achievements()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var db = new WorkLensDbContext(options)) await db.Database.EnsureCreatedAsync();
        var service = new AnnualReviewService(new Factory(options));
        var review = await service.CreateAsync(new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 31));
        await service.SaveAchievementAsync(new AnnualAchievement { ReviewId = review.Id, Title = "已確認成果", Contribution = "完成交付", IsConfirmed = true });
        await service.SaveAchievementAsync(new AnnualAchievement { ReviewId = review.Id, Title = "待確認成果", Contribution = "不應出現" });

        var result = await service.GenerateDraftAsync(review.Id);

        Assert.Contains("已確認成果", result.DraftBody);
        Assert.DoesNotContain("待確認成果", result.DraftBody);
    }

    [Fact]
    public async Task Create_rejects_a_period_longer_than_one_year_or_in_the_future()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using (var db = new WorkLensDbContext(options)) await db.Database.EnsureCreatedAsync();
        var service = new AnnualReviewService(new Factory(options));
        var today = DateOnly.FromDateTime(DateTime.Today);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(today.AddYears(-1), today));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(today, today.AddDays(1)));
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
