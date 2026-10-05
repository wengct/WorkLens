using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class McpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Protocol_result_sets_error_flag_and_avoids_duplicate_body(bool success)
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { success, code = "test", message = "brief", data = "protected context" }),
            Content = [new TextContentBlock { Text = "protected context" }]
        };
        McpToolResponse.Normalize(result);
        Assert.Equal(!success, result.IsError);
        Assert.Equal("brief", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Protocol_result_rejects_oversized_envelope_without_returning_context()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { success = true, message = "brief", data = new string('x', 256 * 1024) })
        };
        McpToolResponse.Normalize(result);
        Assert.True(result.IsError);
        Assert.DoesNotContain(new string('x', 100), JsonSerializer.Serialize(result));
        Assert.Contains("context_too_large", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost:5077", null, true)]
    [InlineData("::1", "[::1]:5077", null, true)]
    [InlineData("192.0.2.1", "localhost:5077", null, false)]
    [InlineData("127.0.0.1", "evil.example:5077", null, false)]
    [InlineData("127.0.0.1", "localhost:5077", "http://evil.example:5077", false)]
    [InlineData("127.0.0.1", "localhost:5077", "http://localhost:5078", false)]
    [InlineData("127.0.0.1", "localhost:5077", "http://localhost:5077", true)]
    public void Access_restricts_remote_hosts_and_origins(string address, string host, string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        Assert.Equal(expected, McpLocalAccess.IsAllowed(context));
    }

    [Fact]
    public async Task Context_requires_no_provider_and_does_not_create_report()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WorkLogs.AddAsync(fixture.Date, 2, "visible");
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var excluded = new Project { Name = "excluded", IncludeInAi = false };
            db.Projects.Add(excluded);
            db.WorkEntries.Add(new WorkEntry { WorkDate = fixture.Date, Hours = 3, WorkContent = "private", ProjectId = excluded.Id });
            await db.SaveChangesAsync();
        }
        var context = await fixture.Reports.GetShareableDailyContextAsync(fixture.Date);
        Assert.Contains("visible", context.Input);
        Assert.DoesNotContain("private", context.Input);
        Assert.Equal(2, context.TotalHours);
        Assert.Null(context.ReportId);
        await using var check = fixture.Factory.CreateDbContext();
        Assert.Empty(await check.Reports.ToListAsync());
        Assert.Empty(await check.AiJobs.ToListAsync());
    }

    [Fact]
    public async Task Scan_failure_never_returns_original_or_error_details()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WorkLogs.AddAsync(fixture.Date, 2, "SECRET");
        var tools = fixture.Tools(new FailedSanitizer());
        var result = await tools.GetDailyContextAsync("2026-10-05", default);
        Assert.False(result.Success);
        Assert.Equal("sanitization_failed", result.Code);
        Assert.Null(result.Data);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Sanitized_context_exposes_no_preview_and_validates_size()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WorkLogs.AddAsync(fixture.Date, 2, "SECRET");
        var tools = fixture.Tools(new CleanSanitizer());
        var result = await tools.GetDailyContextAsync("2026-10-05", default);
        Assert.True(result.Success);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result.Data));
        await fixture.WorkLogs.AddAsync(fixture.Date, 1, new string('x', 256 * 1024));
        Assert.Equal("context_too_large", (await tools.GetDailyContextAsync("2026-10-05", default)).Code);
    }

    [Fact]
    public async Task Saving_preserves_previous_and_rejects_stale_or_null_versions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Reports.SaveExternalDailySummaryAsync(fixture.Date, "first", null);
        var second = await fixture.Reports.SaveExternalDailySummaryAsync(fixture.Date, "second", first.UpdateVersion);
        Assert.Equal(first.UpdateVersion + 1, second.UpdateVersion);
        var previous = await fixture.Reports.GetPreviousRevisionAsync(first.Id);
        Assert.Equal("first", previous!.Body);
        Assert.Null(second.AiJobId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Reports.SaveExternalDailySummaryAsync(fixture.Date, "bad", first.UpdateVersion));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Reports.SaveExternalDailySummaryAsync(fixture.Date, "bad", null));
        await fixture.WorkLogs.AddAsync(fixture.Date, 1, "later");
        Assert.True((await fixture.Reports.GetAsync(first.Id))!.IsStale);
    }

    [Fact]
    public async Task Concurrent_initial_saves_commit_only_one_summary()
    {
        await using var fixture = await Fixture.CreateAsync();
        async Task<bool> Save(string body)
        {
            try
            {
                await fixture.Reports.SaveExternalDailySummaryAsync(fixture.Date, body, null);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Save("first"), Save("second"));
        Assert.Single(results, x => x);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Reports.ToListAsync());
        Assert.Empty(await db.AiJobs.ToListAsync());
    }

    [Fact]
    public async Task Writes_validate_inputs_and_return_only_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tools = fixture.Tools(new CleanSanitizer());
        Assert.Equal("invalid_input", (await tools.CreateWorkEntryAsync("2026-10-05", 25, "SECRET")).Code);
        Assert.Equal("invalid_input", (await tools.CreateWorkEntryAsync("10/5", 2, "SECRET")).Code);
        Assert.Equal("invalid_project", (await tools.CreateWorkEntryAsync("2026-10-05", 2, "SECRET", projectId: Guid.NewGuid())).Code);
        var created = await tools.CreateWorkEntryAsync("2026-10-05", 2, "SECRET");
        Assert.True(created.Success);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(created));
        var saved = await tools.SaveDailySummaryAsync("2026-10-05", "SECRET", null);
        Assert.True(saved.Success);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(saved));
    }

    private sealed class Factory(DbContextOptions<WorkLensDbContext> options) : IDbContextFactory<WorkLensDbContext>
    {
        public WorkLensDbContext CreateDbContext() => new(options);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; private set; } = null!;
        public Factory Factory { get; private set; } = null!;
        public ReportService Reports { get; private set; } = null!;
        public WorkLogService WorkLogs { get; private set; } = null!;
        public DateOnly Date => new(2026, 10, 5);
        public WorkLensMcpTools Tools(IAiContentSanitizer sanitizer) => new(Reports, WorkLogs, sanitizer, Factory);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new Factory(new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return new Fixture
            {
                Connection = connection, Factory = factory,
                WorkLogs = new(factory, new ReportInvalidationService(factory)),
                Reports = new(factory, new AiProviderOrchestrator(new AiProviderRegistry([]), new CleanSanitizer()), new PromptTemplateService(factory), NullLogger<ReportService>.Instance)
            };
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class FailedSanitizer : IAiContentSanitizer
    {
        public Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiSanitizerStatus(false, "test", "unavailable"));
        public Task<AiSanitizationResult> PrepareAsync(AiReportRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AiSanitizationResult(null, new(AiSanitizationStatus.Failed, "test", [], "SECRET")));
    }

    private sealed class CleanSanitizer : IAiContentSanitizer
    {
        public Task<AiSanitizerStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiSanitizerStatus(true, "test", "ok"));
        public Task<AiSanitizationResult> PrepareAsync(AiReportRequest request, CancellationToken cancellationToken = default)
        {
            var summary = new AiSanitizationSummary(AiSanitizationStatus.Redacted, "test", []);
            return Task.FromResult(new AiSanitizationResult(new AiPreparedRequest(request.ReportId, request.Target, request.InputMarkdown.Replace("SECRET", "[已遮蔽]"), request.WorkEntryIds, request.TotalHours, null, "", summary, request.InputFormat), summary));
        }
    }
}
