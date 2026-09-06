using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ReportServiceAiConfigurationTests
{
    [Fact]
    public async Task GenerateWithAi_uses_only_the_default_provider_configuration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var reportId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.AiProviders.AddRange(
                new AiProviderConfiguration { Name = "非預設", ProviderType = "unused" },
                new AiProviderConfiguration { Name = "預設", ProviderType = "selected", IsDefault = true });
            setup.Reports.Add(new ReportDocument
            {
                Id = reportId,
                Kind = ReportKind.Daily,
                PeriodKey = "2026-09-04",
                PeriodStart = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
                PeriodEnd = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero)
            });
            await setup.SaveChangesAsync();
        }
        var unused = new RecordingAdapter("unused");
        var selected = new RecordingAdapter("selected");
        var factory = new Factory(options);
        var service = new ReportService(
            factory,
            new AiProviderOrchestrator(new AiProviderRegistry([unused, selected]), new TestSanitizer()),
            new PromptTemplateService(factory),
            NullLogger<ReportService>.Instance);

        var result = await service.GenerateWithAiAsync(reportId);

        Assert.True(result.Succeeded);
        Assert.False(unused.WasCalled);
        Assert.True(selected.WasCalled);
    }

    [Fact]
    public async Task GenerateWithAi_reports_missing_default_instead_of_throwing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var reportId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.Reports.Add(new ReportDocument { Id = reportId });
            await setup.SaveChangesAsync();
        }
        var factory = new Factory(options);
        var service = new ReportService(
            factory,
            new AiProviderOrchestrator(new AiProviderRegistry([]), new TestSanitizer()),
            new PromptTemplateService(factory),
            NullLogger<ReportService>.Instance);

        var result = await service.GenerateWithAiAsync(reportId);

        Assert.False(result.Succeeded);
        Assert.Contains("預設 AI 設定", result.Error);
    }

    [Fact]
    public async Task GenerateWithAi_includes_the_project_name_in_the_ai_context()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var reportId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.AiProviders.Add(new AiProviderConfiguration
            {
                Name = "預設",
                ProviderType = "selected",
                IsDefault = true
            });
            setup.Projects.Add(new Project
            {
                Id = projectId,
                Name = "WorkLens 專案",
                IncludeInAi = true
            });
            setup.WorkEntries.Add(new WorkEntry
            {
                WorkDate = new DateOnly(2026, 9, 4),
                Hours = 2,
                Title = "補上專案上下文",
                ProjectId = projectId
            });
            setup.Reports.Add(new ReportDocument
            {
                Id = reportId,
                Kind = ReportKind.Daily,
                PeriodKey = "2026-09-04",
                PeriodStart = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
                PeriodEnd = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
                TotalHours = 2
            });
            await setup.SaveChangesAsync();
        }
        var selected = new RecordingAdapter("selected");
        var factory = new Factory(options);
        var service = new ReportService(
            factory,
            new AiProviderOrchestrator(new AiProviderRegistry([selected]), new TestSanitizer()),
            new PromptTemplateService(factory),
            NullLogger<ReportService>.Instance);

        var result = await service.GenerateWithAiAsync(reportId);

        Assert.True(result.Succeeded);
        Assert.NotNull(selected.CapturedRequest);
        Assert.Contains("專案=WorkLens 專案", selected.CapturedRequest.InputMarkdown, StringComparison.Ordinal);
    }

    private sealed class RecordingAdapter(string providerType) : IAiProviderAdapter
    {
        public string ProviderType => providerType;
        public bool WasCalled { get; private set; }
        public AiPreparedRequest? CapturedRequest { get; private set; }

        public Task<AiProviderValidationResult> ValidateAsync(
            AiProviderConfiguration configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiProviderValidationResult(true, "Ready", "Ready"));

        public Task<AiReportResult> GenerateAsync(
            AiProviderConfiguration configuration,
            AiPreparedRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            CapturedRequest = request;
            return Task.FromResult(new AiReportResult(true, "AI result", "raw", null));
        }

        public Task<AiConnectionTestResult> TestConnectionAsync(
            AiProviderConfiguration configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AiConnectionTestResult(true, "ok", null));
    }

    private sealed class TestSanitizer : IAiContentSanitizer
    {
        public Task<AiSanitizationResult> PrepareAsync(
            AiReportRequest request,
            CancellationToken cancellationToken = default)
        {
            var summary = new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []);
            return Task.FromResult(new AiSanitizationResult(
                new AiPreparedRequest(
                    request.ReportId,
                    request.Target,
                    request.InputMarkdown,
                    request.WorkEntryIds,
                    request.TotalHours,
                    request.ExecutablePath,
                    request.EffectivePrompt,
                    summary),
                summary));
        }

        public Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiSanitizerStatus(true, "test", "測試掃描器"));
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options)
        : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }
}
