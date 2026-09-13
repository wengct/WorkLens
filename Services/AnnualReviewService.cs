using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed record AnnualReviewCoverage(DateOnly Month, int WorkEntryCount, int EvidenceCount, double Hours);

public sealed record AnnualReviewDetails(
    AnnualReview Review,
    IReadOnlyList<AnnualAchievement> Achievements,
    IReadOnlyDictionary<Guid, IReadOnlyList<AnnualAchievementEvidence>> Evidence,
    IReadOnlyList<AnnualReviewCoverage> Coverage);

public sealed class AnnualReviewService(IDbContextFactory<WorkLensDbContext> factory)
{
    public async Task<IReadOnlyList<AnnualReview>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AnnualReviews.AsNoTracking().OrderByDescending(x => x.EndDate).ToListAsync(cancellationToken);
    }

    public async Task<AnnualReview> CreateAsync(DateOnly startDate, DateOnly endDate, string? name = null, CancellationToken cancellationToken = default)
    {
        ValidatePeriod(startDate, endDate);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var review = new AnnualReview
        {
            StartDate = startDate,
            EndDate = endDate,
            Name = string.IsNullOrWhiteSpace(name) ? $"{startDate:yyyy/MM/dd}－{endDate:yyyy/MM/dd} 年度回顧" : name.Trim()
        };
        db.AnnualReviews.Add(review);
        await db.SaveChangesAsync(cancellationToken);
        return review;
    }

    public async Task<AnnualReviewDetails?> GetDetailsAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var review = await db.AnnualReviews.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reviewId, cancellationToken);
        if (review is null) return null;
        var achievements = (await db.AnnualAchievements.AsNoTracking().Where(x => x.ReviewId == reviewId)
            .ToListAsync(cancellationToken)).OrderByDescending(x => x.IsConfirmed).ThenBy(x => x.CreatedAt).ToList();
        var ids = achievements.Select(x => x.Id).ToArray();
        var references = await db.AnnualAchievementEvidence.AsNoTracking().Where(x => ids.Contains(x.AchievementId)).ToListAsync(cancellationToken);
        return new AnnualReviewDetails(review, achievements,
            references.GroupBy(x => x.AchievementId).ToDictionary(x => x.Key, x => (IReadOnlyList<AnnualAchievementEvidence>)x.OrderBy(x => x.OccurredOn).ToList()),
            await GetCoverageAsync(db, review, cancellationToken));
    }

    public async Task<AnnualAchievement> SaveAchievementAsync(AnnualAchievement input, CancellationToken cancellationToken = default)
    {
        input.Title = input.Title.Trim();
        if (string.IsNullOrWhiteSpace(input.Title)) throw new ArgumentException("成果標題不可空白。", nameof(input));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var review = await db.AnnualReviews.SingleOrDefaultAsync(x => x.Id == input.ReviewId, cancellationToken)
            ?? throw new ArgumentException("找不到年度回顧。", nameof(input));
        var existing = input.Id == Guid.Empty ? null : await db.AnnualAchievements.SingleOrDefaultAsync(x => x.Id == input.Id, cancellationToken);
        if (existing is null)
        {
            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.UpdatedAt = input.CreatedAt;
            db.AnnualAchievements.Add(input);
            existing = input;
        }
        else
        {
            if (existing.UpdateVersion != input.UpdateVersion) throw new DbUpdateConcurrencyException("成果已由其他頁面更新，請重新載入。");
            Copy(input, existing);
            existing.UpdateVersion++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        review.UpdatedAt = DateTimeOffset.UtcNow;
        review.UpdateVersion++;
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task DeleteAchievementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var achievement = await db.AnnualAchievements.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (achievement is null) return;
        db.AnnualAchievementEvidence.RemoveRange(db.AnnualAchievementEvidence.Where(x => x.AchievementId == id));
        db.AnnualAchievements.Remove(achievement);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AnnualReview> GenerateDraftAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var review = await db.AnnualReviews.SingleAsync(x => x.Id == reviewId, cancellationToken);
        var achievements = await db.AnnualAchievements.AsNoTracking().Where(x => x.ReviewId == reviewId && x.IsConfirmed && !x.IsExcluded).ToListAsync(cancellationToken);
        var body = new System.Text.StringBuilder($"# {review.Name}\n\n## 年度摘要\n\n");
        body.AppendLine(achievements.Count == 0 ? "尚未確認成果；請先確認要納入考核的成果。" : $"本期共確認 {achievements.Count} 項主要成果。\n\n## 主要成果");
        foreach (var item in achievements)
        {
            body.AppendLine($"\n### {item.Title}\n");
            if (!string.IsNullOrWhiteSpace(item.Background)) body.AppendLine($"- 背景：{item.Background}");
            if (!string.IsNullOrWhiteSpace(item.Contribution)) body.AppendLine($"- 個人貢獻：{item.Contribution}");
            if (!string.IsNullOrWhiteSpace(item.Outcome)) body.AppendLine($"- 產出與影響：{item.Outcome}");
        }
        body.AppendLine("\n## 協作貢獻\n\n請補充跨團隊協作、支援與知識分享。\n\n## 成長與後續目標\n\n請補充能力成長與下一年度目標。");
        review.DraftBody = body.ToString();
        review.UpdateVersion++;
        review.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return review;
    }

    public async Task<AnnualReview> UpdateDraftAsync(Guid reviewId, string body, int version, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var review = await db.AnnualReviews.SingleAsync(x => x.Id == reviewId, cancellationToken);
        if (review.UpdateVersion != version) throw new DbUpdateConcurrencyException("草稿已由其他頁面更新，請重新載入。");
        review.DraftBody = body ?? string.Empty;
        review.UpdateVersion++;
        review.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return review;
    }

    private static async Task<IReadOnlyList<AnnualReviewCoverage>> GetCoverageAsync(WorkLensDbContext db, AnnualReview review, CancellationToken ct)
    {
        var entries = await db.WorkEntries.AsNoTracking().Where(x => x.WorkDate >= review.StartDate && x.WorkDate <= review.EndDate).ToListAsync(ct);
        entries.AddRange((await db.RemoteWorkEntries.AsNoTracking().Where(x => !x.IsDeleted && x.WorkDate >= review.StartDate && x.WorkDate <= review.EndDate).ToListAsync(ct)).Select(ActivityQueryService.ToWorkEntry));
        var start = new DateTimeOffset(review.StartDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(review.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var evidence = (await db.SourceEvidence.AsNoTracking().ToListAsync(ct)).Where(x => x.OccurredAt >= start && x.OccurredAt < end).ToList();
        evidence.AddRange((await db.RemoteSourceEvidence.AsNoTracking().Where(x => !x.IsDeleted).ToListAsync(ct)).Where(x => x.OccurredAt >= start && x.OccurredAt < end).Select(ActivityQueryService.ToEvidence));
        return Months(review.StartDate, review.EndDate).Select(month => new AnnualReviewCoverage(month,
            entries.Count(x => x.WorkDate.Year == month.Year && x.WorkDate.Month == month.Month),
            evidence.Count(x => x.OccurredAt.Year == month.Year && x.OccurredAt.Month == month.Month),
            entries.Where(x => x.WorkDate.Year == month.Year && x.WorkDate.Month == month.Month).Sum(x => x.Hours))).ToList();
    }

    private static IEnumerable<DateOnly> Months(DateOnly start, DateOnly end)
    {
        for (var value = new DateOnly(start.Year, start.Month, 1); value <= end; value = value.AddMonths(1)) yield return value;
    }
    private static void ValidatePeriod(DateOnly start, DateOnly end)
    {
        if (end < start || end > DateOnly.FromDateTime(DateTime.Today) || end > start.AddYears(1).AddDays(-1)) throw new ArgumentException("期間需包含起訖日、不得晚於今天，且最長一年。");
    }
    private static void Copy(AnnualAchievement from, AnnualAchievement to)
    {
        to.Title = from.Title; to.Period = from.Period; to.ProjectOrTheme = from.ProjectOrTheme; to.Background = from.Background;
        to.Contribution = from.Contribution; to.Outcome = from.Outcome; to.DeliveryStatus = from.DeliveryStatus;
        to.IsConfirmed = from.IsConfirmed; to.IsExcluded = from.IsExcluded;
    }
}
