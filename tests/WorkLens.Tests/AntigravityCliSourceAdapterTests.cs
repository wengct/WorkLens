using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkLens.Data;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AntigravityCliSourceAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    public AntigravityCliSourceAdapterTests() => Directory.CreateDirectory(Path.Combine(root, "brain"));

    [Fact]
    public async Task Collection_preserves_visible_order_and_excludes_reasoning_and_tools()
    {
        await WriteSessionAsync("session-1",
            Row("USER_INPUT", "context <USER_REQUEST>修正登入</USER_REQUEST> context"),
            """{"type":"PLANNER_RESPONSE","timestamp":"2026-09-02T01:02:00Z","content":"已修正","reasoning":"PRIVATE_REASONING","tool_calls":[{"args":"PRIVATE_TOOL"}]}""",
            Row("RUN_COMMAND", "PRIVATE_OUTPUT"), Row("PLANNER_RESPONSE", ""),
            Row("USER_INPUT", "補上測試"), Row("FUTURE_EVENT", "PRIVATE_UNKNOWN"));
        var usage = Path.Combine(root, "usage");
        Directory.CreateDirectory(usage);
        await File.WriteAllTextAsync(Path.Combine(usage, "usage-2026-09-02.jsonl"),
            """{"session_id":"session-1","cwd":"/sample/project","version":"1.1.10"}""");
        var source = Source();
        var batch = await CollectAsync(source);
        var evidence = Assert.Single(batch.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeAntigravityCliMetadata(evidence.MetadataJson)!;
        Assert.Equal(EvidenceKind.AntigravityCliSession, evidence.Kind);
        Assert.Equal($"session:{source.Id:N}:session-1", evidence.ExternalKey);
        Assert.Equal("修正登入", evidence.Title);
        Assert.Equal(new[] { "user", "assistant", "user" }, metadata.Messages.Select(message => message.Role));
        Assert.Equal("補上測試", metadata.Messages.Last().Text);
        Assert.Equal("/sample/project", metadata.Cwd);
        Assert.Equal("1.1.10", metadata.CliVersion);
        Assert.True(metadata.IsComplete);
        Assert.DoesNotContain("PRIVATE_", evidence.MetadataJson);
        Assert.DoesNotContain("已修正", evidence.CommitMessage);
        Assert.Empty(batch.Warnings);
    }

    [Theory]
    [InlineData("{unfinished")]
    [InlineData("{\"type\":\"PLANNER_RESPONSE\",\"content\":\"missing time\"}")]
    public async Task Partial_transcript_is_marked_and_retried_even_when_its_mtime_is_old(string broken)
    {
        var path = await WriteSessionAsync("partial", Row("USER_INPUT", "有效內容"), broken);
        var source = Source();
        var batch = await CollectAsync(source);
        Assert.False(SourceSettingsSerializer.DeserializeAntigravityCliMetadata(Assert.Single(batch.Evidence).MetadataJson)!.IsComplete);
        Assert.NotEmpty(batch.Warnings);
        source.CheckpointJson = batch.CheckpointJson;
        source.LastSuccessAt = DateTimeOffset.UtcNow;
        await File.WriteAllLinesAsync(path, [Row("USER_INPUT", "有效內容"), Row("PLANNER_RESPONSE", "修復後")]);
        File.SetLastWriteTimeUtc(path, Since.AddDays(-10).UtcDateTime);
        var retried = await CollectAsync(source);
        Assert.True(SourceSettingsSerializer.DeserializeAntigravityCliMetadata(Assert.Single(retried.Evidence).MetadataJson)!.IsComplete);
    }

    [Fact]
    public async Task Initial_import_and_backfill_use_session_time_and_do_not_change_backfill_checkpoint()
    {
        var path = await WriteSessionAsync("old-mtime", Row("USER_INPUT", "歷史會話"));
        File.SetLastWriteTimeUtc(path, Since.AddDays(-10).UtcDateTime);
        var source = Source();
        Assert.Single((await CollectAsync(source)).Evidence);
        var adapter = Adapter();
        var start = DateTimeOffset.Parse("2026-09-02T01:00:00Z");
        var excluded = await adapter.CollectAsync(new CollectionRequest(source, Since, start, false), CancellationToken.None);
        Assert.Empty(excluded.Evidence);
        var included = await adapter.CollectAsync(new CollectionRequest(source, start, start.AddDays(1), false), CancellationToken.None);
        Assert.Single(included.Evidence);
        Assert.Equal(source.CheckpointJson, included.CheckpointJson);
        Assert.Empty((await adapter.CollectAsync(new CollectionRequest(source, start.AddMinutes(1)), CancellationToken.None)).Evidence);
    }

    [Fact]
    public async Task Repeated_import_updates_one_record_and_partial_reads_keep_complete_content()
    {
        var path = await WriteSessionAsync("ongoing", Row("USER_INPUT", "第一輪"), Row("PLANNER_RESPONSE", "回答"));
        var source = Source();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkLensDbContext>().UseSqlite(connection).Options;
        await using var db = new WorkLensDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.ActivitySources.Add(source);
        await db.SaveChangesAsync();
        async Task ImportAsync()
        {
            var batch = await CollectAsync(source);
            var method = typeof(SourceOrchestrator).GetMethod("UpsertEvidenceAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await (Task)method.Invoke(null, [db, batch.Evidence, CancellationToken.None])!;
            await db.SaveChangesAsync();
        }
        await ImportAsync();
        var first = Assert.Single(await db.SourceEvidence.ToListAsync());
        var id = first.Id;
        await ImportAsync();
        Assert.Equal(id, Assert.Single(await db.SourceEvidence.ToListAsync()).Id);
        await File.WriteAllLinesAsync(path, [Row("USER_INPUT", "第一輪"), "{partial"]);
        await ImportAsync();
        Assert.Contains("回答", SourceSettingsSerializer.DeserializeAntigravityCliMetadata(first.MetadataJson)!.Messages.Select(message => message.Text));
        await File.WriteAllLinesAsync(path, [Row("USER_INPUT", "第一輪"), Row("PLANNER_RESPONSE", "回答"), Row("USER_INPUT", "第二輪")]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        await ImportAsync();
        Assert.Equal(3, SourceSettingsSerializer.DeserializeAntigravityCliMetadata(first.MetadataJson)!.Messages.Count);
        Assert.Single(await db.SourceEvidence.ToListAsync());
    }

    [Fact]
    public async Task Different_sources_do_not_overwrite_each_others_session()
    {
        await WriteSessionAsync("shared", Row("USER_INPUT", "對話"));
        Assert.NotEqual(Assert.Single((await CollectAsync(Source())).Evidence).ExternalKey,
            Assert.Single((await CollectAsync(Source())).Evidence).ExternalKey);
    }

    [Fact]
    public async Task Empty_missing_and_unknown_sources_have_distinct_results()
    {
        var source = Source();
        Assert.Empty((await CollectAsync(source)).Evidence);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var empty = await Adapter().ValidateAsync(source, CancellationToken.None);
            Assert.True(empty.IsValid);
            Assert.Contains("尚無對話", empty.Summary);
        }
        await WriteSessionAsync("unknown", "{\"type\":\"unsupported\"}");
        Assert.Contains((await CollectAsync(source)).Warnings, warning => warning.Contains("格式"));
        var missing = new AntigravityCliSourceAdapter(new TestResolver(Path.Combine(root, "missing")), LocalType);
        Assert.Equal(0, (await missing.CollectAsync(new CollectionRequest(source, Since), CancellationToken.None)).SuccessfulRepositories);
    }

    [Fact]
    public async Task Cancellation_does_not_return_a_partial_batch()
    {
        await WriteSessionAsync("cancel", Row("USER_INPUT", "取消"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter().CollectAsync(new CollectionRequest(Source(), Since), cancellation.Token));
    }

    [Theory]
    [InlineData(ActivitySourceType.WindowsAntigravityCli)]
    [InlineData(ActivitySourceType.MacOsAntigravityCli)]
    public async Task Native_settings_round_trip_and_resolve_without_calling_cli(ActivitySourceType type)
    {
        var settings = new AntigravityCliSourceSettings { AntigravityCliHome = root };
        var restored = SourceSettingsSerializer.DeserializeAntigravityCli(SourceSettingsSerializer.Serialize(settings));
        Assert.Equal(root, restored.AntigravityCliHome);
        var source = new ActivitySource { SourceType = type, SettingsJson = SourceSettingsSerializer.Serialize(restored) };
        var runner = new RecordingRunner();
        var resolved = await new AntigravityCliHomeResolver(runner).ResolveAsync(type, source, CancellationToken.None);
        Assert.Equal(root, resolved.Path);
        Assert.Null(runner.Request);
        Assert.Equal(16, (int)ActivitySourceType.WindowsVisualStudioCopilot);
        Assert.Equal(16, (int)EvidenceKind.CopilotSession);
    }

    [Fact]
    public async Task Wsl_requires_distro_and_passes_custom_path_as_one_argument()
    {
        var runner = new RecordingRunner();
        var resolver = new AntigravityCliHomeResolver(runner);
        var source = new ActivitySource { SourceType = ActivitySourceType.WslAntigravityCli };
        Assert.False((await resolver.ResolveAsync(source.SourceType, source, CancellationToken.None)).Succeeded);
        var settings = new AntigravityCliSourceSettings { Distro = "Ubuntu" };
        source.SettingsJson = SourceSettingsSerializer.Serialize(settings);
        Assert.True((await resolver.ResolveAsync(source.SourceType, source, CancellationToken.None)).Succeeded);
        Assert.Contains(runner.Request!.Arguments, argument => argument.Contains("$HOME/.gemini/antigravity-cli"));
        settings.AntigravityCliHome = "/home/test/含 空白;literal";
        source.SettingsJson = SourceSettingsSerializer.Serialize(settings);
        await resolver.ResolveAsync(source.SourceType, source, CancellationToken.None);
        Assert.Equal(settings.AntigravityCliHome, runner.Request!.Arguments.Last());
        Assert.DoesNotContain("sh", runner.Request.Arguments);
    }

    private static ActivitySourceType LocalType => OperatingSystem.IsMacOS() ? ActivitySourceType.MacOsAntigravityCli : ActivitySourceType.WindowsAntigravityCli;
    private ActivitySource Source() => new() { SourceType = LocalType, Enabled = true };
    private AntigravityCliSourceAdapter Adapter() => new(new TestResolver(root), LocalType);
    private Task<CollectionBatch> CollectAsync(ActivitySource source) => Adapter().CollectAsync(new CollectionRequest(source, Since), CancellationToken.None);
    private static string Row(string type, string content) => JsonSerializer.Serialize(new { type, content, created_at = "2026-09-02T01:00:00Z" });
    private async Task<string> WriteSessionAsync(string id, params string[] rows)
    {
        var directory = Path.Combine(root, "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transcript_full.jsonl");
        await File.WriteAllLinesAsync(path, rows);
        return path;
    }
    public void Dispose() => Directory.Delete(root, true);
    private sealed class TestResolver(string path) : IAntigravityCliHomeResolver
    {
        public Task<AntigravityCliHomeResolution> ResolveAsync(ActivitySourceType type, ActivitySource source, CancellationToken cancellationToken) =>
            Task.FromResult(AntigravityCliHomeResolution.Success(path));
    }
    private sealed class RecordingRunner : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ProcessResult(0, @"\\wsl.localhost\Ubuntu\home\test\.gemini\antigravity-cli", ""));
        }
    }
}
