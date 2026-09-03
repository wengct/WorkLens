using System.Text.Json;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class CodexSourceAdapterTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "WorkLens.Tests",
        Guid.NewGuid().ToString("N"));

    public CodexSourceAdapterTests()
    {
        Directory.CreateDirectory(Path.Combine(testRoot, "sessions", "2026", "09", "02"));
        Directory.CreateDirectory(Path.Combine(testRoot, "archived_sessions"));
    }

    [Fact]
    public async Task Validation_accepts_readable_local_codex_home()
    {
        var adapter = CreateAdapter();
        var source = CreateSource();

        var result = await adapter.ValidateAsync(source, CancellationToken.None);

        Assert.True(result.IsValid, result.Summary);
        Assert.Contains(testRoot, result.Details);
    }

    [Fact]
    public async Task Collection_preserves_visible_transcript_but_context_contains_only_user_messages()
    {
        var id = Guid.NewGuid();
        var file = SessionPath(id);
        await File.WriteAllLinesAsync(file,
        [
            Row("2026-09-02T01:00:00Z", "session_meta", new
            {
                session_id = id,
                timestamp = "2026-09-02T01:00:00Z",
                cwd = @"C:\Work\Repo",
                cli_version = "1.2.3"
            }),
            Row("2026-09-02T01:01:00Z", "event_msg", new
            {
                type = "user_message",
                client_id = "user-1",
                message = "請新增功能",
                images = new[] { "image" }
            }),
            Row("2026-09-02T01:02:00Z", "event_msg", new
            {
                type = "agent_message",
                message = "功能已完成"
            }),
            Row("2026-09-02T01:03:00Z", "response_item", new
            {
                type = "message",
                id = "internal-user",
                role = "user",
                content = new[] { new { type = "input_text", text = "<environment_context>internal</environment_context>" } }
            }),
            Row("2026-09-02T01:04:00Z", "response_item", new
            {
                type = "custom_tool_call",
                input = "secret tool input"
            })
        ]);
        await File.WriteAllLinesAsync(Path.Combine(testRoot, "session_index.jsonl"),
        [
            JsonSerializer.Serialize(new { id, thread_name = "舊標題", updated_at = "2026-09-02T01:00:00Z" }),
            JsonSerializer.Serialize(new { id, thread_name = "新增 Codex 來源", updated_at = "2026-09-02T01:05:00Z" })
        ]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal(EvidenceKind.CodexSession, evidence.Kind);
        Assert.Equal("codex-session", evidence.RepositoryKey);
        Assert.Equal($"session:{id:D}", evidence.ExternalKey);
        Assert.Equal("新增 Codex 來源", evidence.Title);
        Assert.Equal("請新增功能", evidence.CommitMessage);
        Assert.DoesNotContain("功能已完成", evidence.CommitMessage);
        Assert.DoesNotContain("secret", evidence.MetadataJson, StringComparison.OrdinalIgnoreCase);
        var metadata = SourceSettingsSerializer.DeserializeCodexMetadata(evidence.MetadataJson);
        Assert.NotNull(metadata);
        Assert.Equal(2, metadata.Messages.Count);
        Assert.Equal(["user", "assistant"], metadata.Messages.Select(message => message.Role));
        Assert.Equal(1, metadata.AttachmentCount);
        Assert.Equal(@"C:\Work\Repo", metadata.Cwd);
    }

    [Fact]
    public async Task New_item_completed_format_wins_over_duplicate_response_items()
    {
        var id = Guid.NewGuid();
        await File.WriteAllLinesAsync(SessionPath(id),
        [
            Row("2026-09-02T02:00:00Z", "session_meta", new { id, timestamp = "2026-09-02T02:00:00Z" }),
            Row("2026-09-02T02:01:00Z", "event_msg", new
            {
                type = "item_completed",
                item = new
                {
                    type = "UserMessage",
                    id = "user-new",
                    content = new[] { new { type = "text", text = "新版使用者訊息" } }
                }
            }),
            Row("2026-09-02T02:02:00Z", "event_msg", new
            {
                type = "item_completed",
                item = new
                {
                    type = "AgentMessage",
                    id = "assistant-same",
                    content = new[] { new { type = "Text", text = "新版助理訊息" } }
                }
            }),
            Row("2026-09-02T02:02:00Z", "response_item", new
            {
                type = "message",
                id = "assistant-same",
                role = "assistant",
                content = new[] { new { type = "output_text", text = "新版助理訊息" } }
            })
        ]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeCodexMetadata(evidence.MetadataJson)!;
        Assert.Equal(2, metadata.Messages.Count);
        Assert.Equal("新版使用者訊息", evidence.CommitMessage);
    }

    [Fact]
    public async Task Same_session_in_active_and_archive_is_emitted_once()
    {
        var id = Guid.NewGuid();
        var active = SessionPath(id);
        var archived = Path.Combine(testRoot, "archived_sessions", $"rollout-2026-09-02T03-00-00-{id:D}.jsonl");
        var rows = new[]
        {
            Row("2026-09-02T03:00:00Z", "session_meta", new { id, timestamp = "2026-09-02T03:00:00Z" }),
            Row("2026-09-02T03:01:00Z", "event_msg", new { type = "user_message", message = "同一個 session" })
        };
        await File.WriteAllLinesAsync(active, rows);
        await File.WriteAllLinesAsync(archived, rows);
        File.SetLastWriteTimeUtc(active, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(archived, DateTime.UtcNow.AddMinutes(-1));

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeCodexMetadata(evidence.MetadataJson)!;
        Assert.True(metadata.Archived);
    }

    [Fact]
    public async Task Backfill_filters_by_session_start_and_does_not_replace_checkpoint()
    {
        var included = Guid.NewGuid();
        var excluded = Guid.NewGuid();
        await WriteMinimalSessionAsync(included, "2026-09-02T04:00:00Z", "納入");
        await WriteMinimalSessionAsync(excluded, "2026-08-20T04:00:00Z", "略過");
        var source = CreateSource();
        source.CheckpointJson = "{\"lastScanAt\":\"2026-09-01T00:00:00Z\"}";

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(
                source,
                DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
                DateTimeOffset.Parse("2026-09-03T00:00:00Z"),
                UpdateCheckpoint: false),
            CancellationToken.None);

        Assert.Single(batch.Evidence);
        Assert.Contains(included.ToString("D"), batch.Evidence[0].ExternalKey);
        Assert.Equal(source.CheckpointJson, batch.CheckpointJson);
        Assert.Equal(1, batch.SuccessfulRepositories);
    }

    [Fact]
    public async Task Incremental_collection_updates_an_old_session_when_its_file_changes()
    {
        var id = Guid.NewGuid();
        var file = SessionPath(id);
        await File.WriteAllLinesAsync(file,
        [
            Row("2026-08-01T01:00:00Z", "session_meta", new { id, timestamp = "2026-08-01T01:00:00Z" }),
            Row("2026-08-01T01:01:00Z", "event_msg", new { type = "user_message", message = "原始問題" })
        ]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        var source = CreateSource();
        source.LastSuccessAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        source.CheckpointJson = JsonSerializer.Serialize(new { lastScanAt = DateTimeOffset.UtcNow.AddMinutes(-2) });

        await File.AppendAllLinesAsync(file,
        [
            Row(DateTimeOffset.UtcNow.ToString("O"), "event_msg", new { type = "user_message", message = "今天追加問題" })
        ]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(source, source.LastSuccessAt.Value),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T01:00:00Z"), evidence.OccurredAt);
        Assert.Contains("原始問題", evidence.CommitMessage);
        Assert.Contains("今天追加問題", evidence.CommitMessage);
    }

    [Fact]
    public async Task Wsl_home_resolver_uses_selected_distro_and_custom_linux_path()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(
            0,
            @"\\wsl.localhost\Ubuntu\home\me\custom-codex" + Environment.NewLine,
            string.Empty));
        var resolver = new CodexHomeResolver(runner);
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WslCodex,
            SettingsJson = SourceSettingsSerializer.Serialize(new CodexSourceSettings
            {
                Distro = "Ubuntu",
                CodexHome = "/home/me/custom-codex"
            })
        };

        var result = await resolver.ResolveAsync(
            ActivitySourceType.WslCodex,
            source,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\custom-codex", result.Path);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("wsl.exe", runner.LastRequest.FileName);
        Assert.Contains("Ubuntu", runner.LastRequest.Arguments);
        Assert.Contains("/home/me/custom-codex", runner.LastRequest.Arguments);
    }

    [Fact]
    public async Task Wsl_home_resolver_requires_a_distro()
    {
        var resolver = new CodexHomeResolver(new RecordingProcessRunner(new ProcessResult(0, string.Empty, string.Empty)));
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WslCodex,
            SettingsJson = SourceSettingsSerializer.Serialize(new CodexSourceSettings())
        };

        var result = await resolver.ResolveAsync(
            ActivitySourceType.WslCodex,
            source,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Ubuntu", result.Error);
    }

    [Fact]
    public async Task Mac_home_resolver_accepts_a_native_absolute_path()
    {
        var resolver = new CodexHomeResolver(new RecordingProcessRunner(new ProcessResult(1, string.Empty, "not used")));
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.MacOsCodex,
            SettingsJson = SourceSettingsSerializer.Serialize(new CodexSourceSettings { CodexHome = testRoot })
        };

        var result = await resolver.ResolveAsync(
            ActivitySourceType.MacOsCodex,
            source,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Path.GetFullPath(testRoot), result.Path);
    }

    [Fact]
    public async Task Wsl_home_resolver_can_be_verified_against_an_installed_distro()
    {
        var distro = Environment.GetEnvironmentVariable("WORKLENS_WSL_TEST");
        if (string.IsNullOrWhiteSpace(distro))
        {
            return;
        }

        var resolver = new CodexHomeResolver(new ProcessRunner());
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WslCodex,
            SettingsJson = SourceSettingsSerializer.Serialize(new CodexSourceSettings { Distro = distro })
        };

        var result = await resolver.ResolveAsync(
            ActivitySourceType.WslCodex,
            source,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotEqual(".", result.Path);
        Assert.True(Directory.Exists(result.Path), $"Resolved WSL path is not readable: {result.Path}");
    }

    private CodexSourceAdapter CreateAdapter() =>
        new(new ProcessRunner(), LocalCodexType);

    private ActivitySource CreateSource() => new()
    {
        SourceType = LocalCodexType,
        Enabled = true,
        SettingsJson = SourceSettingsSerializer.Serialize(new CodexSourceSettings { CodexHome = testRoot })
    };

    private static ActivitySourceType LocalCodexType =>
        OperatingSystem.IsMacOS() ? ActivitySourceType.MacOsCodex : ActivitySourceType.WindowsCodex;

    private string SessionPath(Guid id) => Path.Combine(
        testRoot,
        "sessions",
        "2026",
        "09",
        "02",
        $"rollout-2026-09-02T01-00-00-{id:D}.jsonl");

    private async Task WriteMinimalSessionAsync(Guid id, string timestamp, string message)
    {
        await File.WriteAllLinesAsync(SessionPath(id),
        [
            Row(timestamp, "session_meta", new { id, timestamp }),
            Row(timestamp, "event_msg", new { type = "user_message", message })
        ]);
    }

    private static string Row(string timestamp, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp, type, payload });

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class RecordingProcessRunner(ProcessResult result) : IProcessRunner
    {
        public ProcessRequest? LastRequest { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }
}
