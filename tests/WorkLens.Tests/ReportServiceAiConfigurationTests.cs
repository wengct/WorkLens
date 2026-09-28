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

    [Theory]
    [InlineData(EvidenceKind.CodexSession)]
    [InlineData(EvidenceKind.ClaudeCodeSession)]
    [InlineData(EvidenceKind.CopilotSession)]
    [InlineData(EvidenceKind.AntigravityCliSession)]
    public async Task Weekly_ai_context_uses_each_user_message_time_across_session_and_week_boundaries(EvidenceKind kind)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var previousSunday = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var nextMonday = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var report = new ReportDocument
        {
            Kind = ReportKind.Weekly,
            PeriodKey = "2026-W37",
            PeriodStart = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7))),
            // Existing weekly reports may contain the old extra Monday.
            PeriodEnd = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 15)))
        };
        var source = new ActivitySource { Enabled = true, IncludeInAi = true };
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.AiProviders.Add(new AiProviderConfiguration { Name = "測試", ProviderType = "selected", IsDefault = true });
            setup.ActivitySources.Add(source);
            setup.Reports.Add(report);
            setup.SourceEvidence.Add(new SourceEvidence
            {
                SourceId = source.Id,
                Kind = kind,
                RepositoryKey = "test-session",
                ExternalKey = "cross-week",
                Title = "OUTSIDE_PERIOD_TITLE",
                CommitMessage = "OUTSIDE_PERIOD_USER\nWEEKEND_USER\nNEXT_WEEK_USER",
                OccurredAt = previousSunday,
                MetadataJson = SessionMetadata(kind, previousSunday, saturday, nextMonday)
            });
            await setup.SaveChangesAsync();
        }

        var selected = new RecordingAdapter("selected");
        var factory = new Factory(options);
        var service = new ReportService(factory,
            new AiProviderOrchestrator(new AiProviderRegistry([selected]), new TestSanitizer()),
            new PromptTemplateService(factory), NullLogger<ReportService>.Instance);

        Assert.True((await service.GenerateWithAiAsync(report.Id)).Succeeded);
        var input = selected.CapturedRequest!.InputMarkdown;
        Assert.Contains("WEEKEND_USER", input, StringComparison.Ordinal);
        Assert.Contains($"{saturday.ToLocalTime():O}｜WEEKEND_USER", input, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSIDE_PERIOD_USER", input, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXT_WEEK_USER", input, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSIDE_PERIOD_TITLE", input, StringComparison.Ordinal);
        Assert.DoesNotContain("ASSISTANT_PRIVATE", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Daily_ai_context_uses_only_messages_from_the_selected_day()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var friday = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var sunday = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var report = new ReportDocument
        {
            Kind = ReportKind.Daily,
            PeriodKey = "2026-09-12",
            PeriodStart = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 12))),
            PeriodEnd = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 13)))
        };
        var source = new ActivitySource { Enabled = true, IncludeInAi = true };
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.AiProviders.Add(new AiProviderConfiguration { Name = "測試", ProviderType = "selected", IsDefault = true });
            setup.ActivitySources.Add(source);
            setup.Reports.Add(report);
            setup.SourceEvidence.Add(new SourceEvidence
            {
                SourceId = source.Id,
                Kind = EvidenceKind.CodexSession,
                RepositoryKey = "test-session",
                ExternalKey = "cross-day",
                Title = "OUTSIDE_PERIOD_TITLE",
                CommitMessage = "OUTSIDE_PERIOD_USER\nWEEKEND_USER\nNEXT_WEEK_USER",
                OccurredAt = friday,
                MetadataJson = SessionMetadata(EvidenceKind.CodexSession, friday, saturday, sunday)
            });
            await setup.SaveChangesAsync();
        }

        var selected = new RecordingAdapter("selected");
        var factory = new Factory(options);
        var service = new ReportService(factory,
            new AiProviderOrchestrator(new AiProviderRegistry([selected]), new TestSanitizer()),
            new PromptTemplateService(factory), NullLogger<ReportService>.Instance);

        Assert.True((await service.GenerateWithAiAsync(report.Id)).Succeeded);
        var input = selected.CapturedRequest!.InputMarkdown;
        Assert.Contains("WEEKEND_USER", input, StringComparison.Ordinal);
        Assert.Contains($"{saturday.ToLocalTime():O}｜WEEKEND_USER", input, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSIDE_PERIOD_USER", input, StringComparison.Ordinal);
        Assert.DoesNotContain("NEXT_WEEK_USER", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Visual_studio_copilot_uses_session_time_when_message_times_are_unavailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var sessionTime = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var report = new ReportDocument
        {
            Kind = ReportKind.Daily,
            PeriodKey = "2026-09-13",
            PeriodStart = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 13))),
            PeriodEnd = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14)))
        };
        var source = new ActivitySource { Enabled = true, IncludeInAi = true };
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.AiProviders.Add(new AiProviderConfiguration { Name = "測試", ProviderType = "selected", IsDefault = true });
            setup.ActivitySources.Add(source);
            setup.Reports.Add(report);
            setup.SourceEvidence.Add(new SourceEvidence
            {
                SourceId = source.Id,
                Kind = EvidenceKind.CopilotSession,
                Environment = ActivitySourceType.WindowsVisualStudioCopilot.ToString(),
                RepositoryKey = "visual-studio-session",
                ExternalKey = "without-message-time",
                Title = "VS Copilot",
                CommitMessage = "SESSION_USER_CONTEXT",
                OccurredAt = sessionTime,
                MetadataJson = SourceSettingsSerializer.SerializeCopilotMetadata(new CopilotSessionMetadata
                {
                    Messages = [new CopilotSessionMessage { Role = "user", Timestamp = sessionTime, Text = "METADATA_USER" }]
                })
            });
            await setup.SaveChangesAsync();
        }

        var selected = new RecordingAdapter("selected");
        var factory = new Factory(options);
        var service = new ReportService(factory,
            new AiProviderOrchestrator(new AiProviderRegistry([selected]), new TestSanitizer()),
            new PromptTemplateService(factory), NullLogger<ReportService>.Instance);

        Assert.True((await service.GenerateWithAiAsync(report.Id)).Succeeded);
        var input = selected.CapturedRequest!.InputMarkdown;
        Assert.Contains("SESSION_USER_CONTEXT", input, StringComparison.Ordinal);
        Assert.DoesNotContain("METADATA_USER", input, StringComparison.Ordinal);
    }

    private static string SessionMetadata(EvidenceKind kind, DateTimeOffset before, DateTimeOffset within, DateTimeOffset after) => kind switch
    {
        EvidenceKind.CodexSession => SourceSettingsSerializer.SerializeMetadata(new CodexSessionMetadata
        {
            Messages =
            [
                new CodexSessionMessage { Role = "user", Timestamp = before, Text = "OUTSIDE_PERIOD_USER" },
                new CodexSessionMessage { Role = "user", Timestamp = within, Text = "WEEKEND_USER" },
                new CodexSessionMessage { Role = "assistant", Timestamp = within, Text = "ASSISTANT_PRIVATE" },
                new CodexSessionMessage { Role = "user", Timestamp = after, Text = "NEXT_WEEK_USER" }
            ]
        }),
        EvidenceKind.ClaudeCodeSession => SourceSettingsSerializer.SerializeClaudeCodeMetadata(new ClaudeCodeSessionMetadata
        {
            Messages =
            [
                new ClaudeCodeSessionMessage { Role = "user", Timestamp = before, Text = "OUTSIDE_PERIOD_USER" },
                new ClaudeCodeSessionMessage { Role = "user", Timestamp = within, Text = "WEEKEND_USER" },
                new ClaudeCodeSessionMessage { Role = "assistant", Timestamp = within, Text = "ASSISTANT_PRIVATE" },
                new ClaudeCodeSessionMessage { Role = "user", Timestamp = after, Text = "NEXT_WEEK_USER" }
            ]
        }),
        EvidenceKind.CopilotSession => SourceSettingsSerializer.SerializeCopilotMetadata(new CopilotSessionMetadata
        {
            Messages =
            [
                new CopilotSessionMessage { Role = "user", Timestamp = before, Text = "OUTSIDE_PERIOD_USER" },
                new CopilotSessionMessage { Role = "user", Timestamp = within, Text = "WEEKEND_USER" },
                new CopilotSessionMessage { Role = "assistant", Timestamp = within, Text = "ASSISTANT_PRIVATE" },
                new CopilotSessionMessage { Role = "user", Timestamp = after, Text = "NEXT_WEEK_USER" }
            ]
        }),
        EvidenceKind.AntigravityCliSession => SourceSettingsSerializer.SerializeAntigravityCliMetadata(new AntigravityCliSessionMetadata
        {
            Messages =
            [
                new AntigravityCliSessionMessage { Role = "user", Timestamp = before, Text = "OUTSIDE_PERIOD_USER" },
                new AntigravityCliSessionMessage { Role = "user", Timestamp = within, Text = "WEEKEND_USER" },
                new AntigravityCliSessionMessage { Role = "assistant", Timestamp = within, Text = "ASSISTANT_PRIVATE" },
                new AntigravityCliSessionMessage { Role = "user", Timestamp = after, Text = "NEXT_WEEK_USER" }
            ]
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Antigravity_context_respects_sharing_without_sending_transcript(bool sourceShares, bool projectShares)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        var report = new ReportDocument
        {
            Kind = ReportKind.Daily, PeriodKey = "2026-09-04",
            PeriodStart = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero)
        };
        var project = new Project { Name = "分享測試", IncludeInAi = projectShares };
        var source = new ActivitySource { SourceType = ActivitySourceType.WindowsAntigravityCli, ProjectId = project.Id, Enabled = true, IncludeInAi = sourceShares };
        await using (var setup = new WorkLensDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.AiFeatureSettings.Add(new AiFeatureSettings { Enabled = true });
            setup.AiProviders.Add(new AiProviderConfiguration { Name = "測試", ProviderType = "selected", IsDefault = true });
            setup.Projects.Add(project);
            setup.ActivitySources.Add(source);
            setup.Reports.Add(report);
            setup.SourceEvidence.Add(new SourceEvidence
            {
                SourceId = source.Id, ProjectId = project.Id, Kind = EvidenceKind.AntigravityCliSession,
                RepositoryKey = "antigravity-cli-session", ExternalKey = "sample", Title = "來源標題",
                CommitMessage = "USER_REQUEST_ONLY", OccurredAt = report.PeriodStart.AddHours(1),
                MetadataJson = SourceSettingsSerializer.SerializeAntigravityCliMetadata(new AntigravityCliSessionMetadata
                {
                    Messages = [new AntigravityCliSessionMessage { Role = "assistant", Text = "PRIVATE_ASSISTANT_TRANSCRIPT" }]
                })
            });
            await setup.SaveChangesAsync();
        }
        var selected = new RecordingAdapter("selected");
        var factory = new Factory(options);
        var service = new ReportService(factory,
            new AiProviderOrchestrator(new AiProviderRegistry([selected]), new TestSanitizer()),
            new PromptTemplateService(factory), NullLogger<ReportService>.Instance);
        Assert.True((await service.GenerateWithAiAsync(report.Id)).Succeeded);
        var input = selected.CapturedRequest!.InputMarkdown;
        Assert.Equal(sourceShares && projectShares, input.Contains("USER_REQUEST_ONLY", StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE_ASSISTANT_TRANSCRIPT", input);
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
