using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AnnualReviewAiServiceTests
{
    [Fact]
    public async Task Large_annual_input_is_summarized_in_bounded_segments_before_final_generation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"worklens-annual-ai-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var reviewId = Guid.NewGuid();
        try
        {
            await using (var db = new WorkLensDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.AnnualReviews.Add(new AnnualReview
                {
                    Id = reviewId,
                    Name = "年度回顧",
                    StartDate = new DateOnly(2025, 1, 1),
                    EndDate = new DateOnly(2025, 12, 31)
                });
                await db.SaveChangesAsync();
            }

            var adapter = new SegmentRecordingAdapter();
            var factory = new Factory(options);
            var service = new AnnualReviewAiService(
                factory,
                new AiProviderOrchestrator(new AiProviderRegistry([adapter]), new CleanSanitizer()),
                new PromptTemplateService(factory),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AnnualReviewAiService>.Instance);
            var input = string.Join('\n', Enumerable.Range(1, 1_500).Select(index => $"### 2025/01/{index:0000}｜專案｜完成工作項目 {index}\n保留具體成果與驗證內容。"));
            var request = new AiPreparedRequest(
                reviewId,
                "test",
                input,
                [],
                0,
                null,
                "產生年度草稿。",
                new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));
            var preparation = new AnnualReviewAiPreparation(
                reviewId,
                1,
                new AiProviderConfiguration { ProviderType = "ask-bridge" },
                request,
                null,
                "年度回顧",
                request.EffectivePrompt);
            var progress = new RecordingProgress();

            var result = await service.SendAsync(preparation, progress: progress);

            Assert.True(result.Succeeded, result.Error);
            Assert.True(adapter.Requests.Count > 1);
            Assert.All(adapter.Requests.SkipLast(1), item =>
                Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(item.InputMarkdown), 1, 48 * 1024));
            Assert.All(adapter.Requests.SkipLast(1), item =>
                Assert.Contains(request.EffectivePrompt, item.EffectivePrompt, StringComparison.Ordinal));
            Assert.Contains("分段摘要", adapter.Requests[^1].InputMarkdown, StringComparison.Ordinal);
            Assert.Contains("最終的年度績效草稿", adapter.Requests[^1].EffectivePrompt, StringComparison.Ordinal);
            Assert.Contains(progress.Messages, message => message.Contains("第 1/", StringComparison.Ordinal));
            Assert.Contains(progress.Messages, message => message.Contains("最終年度草稿", StringComparison.Ordinal));
            await using var verification = new WorkLensDbContext(options);
            Assert.Equal("final annual draft", (await verification.AnnualReviews.SingleAsync()).DraftBody);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Attachment_session_reset_is_retried_once_before_saving_the_annual_draft()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"worklens-annual-ai-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<WorkLensDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var reviewId = Guid.NewGuid();
        try
        {
            await using (var db = new WorkLensDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.AnnualReviews.Add(new AnnualReview
                {
                    Id = reviewId,
                    Name = "年度回顧",
                    StartDate = new DateOnly(2025, 1, 1),
                    EndDate = new DateOnly(2025, 12, 31)
                });
                await db.SaveChangesAsync();
            }

            var adapter = new ResetOnceAdapter();
            var factory = new Factory(options);
            var service = new AnnualReviewAiService(
                factory,
                new AiProviderOrchestrator(new AiProviderRegistry([adapter]), new CleanSanitizer()),
                new PromptTemplateService(factory),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AnnualReviewAiService>.Instance);
            var request = new AiPreparedRequest(
                reviewId,
                "test",
                "年度資料",
                [],
                0,
                null,
                "產生年度草稿。",
                new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));
            var preparation = new AnnualReviewAiPreparation(
                reviewId,
                1,
                new AiProviderConfiguration { ProviderType = "ask-bridge" },
                request,
                null,
                "年度回顧",
                request.EffectivePrompt);

            var result = await service.SendAsync(preparation);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, adapter.Calls);
            await using var verification = new WorkLensDbContext(options);
            Assert.Equal("annual draft after retry", (await verification.AnnualReviews.SingleAsync()).DraftBody);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Cancellation_during_generation_does_not_save_the_annual_draft()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new CancellingAdapter(cancellation);
        var factory = new NoDatabaseFactory();
        var service = new AnnualReviewAiService(factory,
            new AiProviderOrchestrator(new AiProviderRegistry([adapter]), new CleanSanitizer()), new PromptTemplateService(factory), Microsoft.Extensions.Logging.Abstractions.NullLogger<AnnualReviewAiService>.Instance);
        var id = Guid.NewGuid();
        var request = new AiPreparedRequest(id, "test", "### first\n" + new string('a', 30_000) + "\n### second\n" + new string('b', 30_000),
            [], 0, null, "", new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));
        var preparation = new AnnualReviewAiPreparation(id, 1, new AiProviderConfiguration { ProviderType = "test" }, request, null, "", "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SendAsync(preparation, cancellationToken: cancellation.Token));
        Assert.Equal(1, adapter.Calls);
        Assert.Same(request, adapter.ReceivedRequest);
        Assert.Equal(cancellation.Token, adapter.ReceivedToken);
    }

    private sealed class NoDatabaseFactory : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => throw new InvalidOperationException("Cancelled generation must not access the database.");
    }

    private sealed class CancellingAdapter(CancellationTokenSource cancellation) : IAiProviderAdapter
    {
        public string ProviderType => "test";
        public int Calls { get; private set; }
        public AiPreparedRequest? ReceivedRequest { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public Task<AiReportResult> GenerateAsync(AiProviderConfiguration configuration, AiPreparedRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedRequest = request;
            ReceivedToken = cancellationToken;
            cancellation.Cancel();
            return Task.FromResult(new AiReportResult(true, "must not save", null, null));
        }
        public Task<AiProviderValidationResult> ValidateAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiConnectionTestResult> TestConnectionAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SegmentRecordingAdapter : IAiProviderAdapter
    {
        public string ProviderType => "ask-bridge";
        public List<AiPreparedRequest> Requests { get; } = [];

        public Task<AiReportResult> GenerateAsync(AiProviderConfiguration configuration, AiPreparedRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.InputMarkdown.Contains("## 分段摘要", StringComparison.Ordinal)
                ? "final annual draft"
                : $"segment summary {Requests.Count}";
            return Task.FromResult(new AiReportResult(true, body, body, null));
        }

        public Task<AiProviderValidationResult> ValidateAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiConnectionTestResult> TestConnectionAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ResetOnceAdapter : IAiProviderAdapter
    {
        public string ProviderType => "ask-bridge";
        public int Calls { get; private set; }

        public Task<AiReportResult> GenerateAsync(AiProviderConfiguration configuration, AiPreparedRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls == 1
                ? new AiReportResult(false, null, null, "Error attaching images/files: MCP tool 'evaluate_script' timed out after 90s (MCP session was reset; re-run the command)")
                : new AiReportResult(true, "annual draft after retry", "annual draft after retry", null));
        }

        public Task<AiProviderValidationResult> ValidateAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiConnectionTestResult> TestConnectionAsync(AiProviderConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    [Fact]
    public async Task Prepare_uses_included_work_and_source_records_without_manual_achievements()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var reviewId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var date = new DateOnly(2025, 3, 10);
        await using (var db = new WorkLensDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            db.AiProviders.Add(new AiProviderConfiguration { IsDefault = true, Name = "測試 AI" });
            db.AnnualReviews.Add(new AnnualReview { Id = reviewId, StartDate = date, EndDate = date, Name = "年度回顧" });
            db.ActivitySources.Add(new ActivitySource { Id = sourceId, DisplayName = "Git", Enabled = true, IncludeInAi = true, HealthStatus = SourceHealthStatus.Ready });
            db.WorkEntries.Add(new WorkEntry { WorkDate = date, Title = "完成登入", WorkContent = "完成登入流程與測試", Hours = 2 });
            db.SourceEvidence.Add(new SourceEvidence { SourceId = sourceId, Title = "修正部署", CommitMessage = "修正部署問題", OccurredAt = new DateTimeOffset(2025, 3, 10, 9, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        var factory = new Factory(options);
        var service = new AnnualReviewAiService(factory, new AiProviderOrchestrator(new AiProviderRegistry([]), new CleanSanitizer()), new PromptTemplateService(factory), Microsoft.Extensions.Logging.Abstractions.NullLogger<AnnualReviewAiService>.Instance);

        var result = await service.PrepareAsync(reviewId, null);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Preparation);
        Assert.Contains($"\"reportId\":\"{reviewId}\"", result.Preparation.Request.EffectivePrompt);
        Assert.Contains("完成登入流程與測試", result.Preparation.Request.InputMarkdown, StringComparison.Ordinal);
        Assert.Contains("修正部署問題", result.Preparation.Request.InputMarkdown, StringComparison.Ordinal);
    }

    private sealed class CleanSanitizer : IAiContentSanitizer
    {
        public Task<AiSanitizationResult> PrepareAsync(AiReportRequest request, CancellationToken cancellationToken = default)
        {
            var summary = new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []);
            return Task.FromResult(new AiSanitizationResult(new AiPreparedRequest(request.ReportId, request.Target, request.InputMarkdown, request.WorkEntryIds, request.TotalHours, request.ExecutablePath, request.EffectivePrompt, summary), summary));
        }

        public Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiSanitizerStatus(true, "test", "test"));
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
