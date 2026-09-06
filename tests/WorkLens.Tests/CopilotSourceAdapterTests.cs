using System.Text.Json;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class CopilotSourceAdapterTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Collection_imports_visible_user_and_assistant_messages()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionDirectory = Path.Combine(testRoot, "session-state", sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllLinesAsync(Path.Combine(sessionDirectory, "events.jsonl"),
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-02T01:00:00Z",
                type = "session.start",
                payload = new { sessionId, createdAt = "2026-09-02T01:00:00Z", cwd = @"C:\Work\Repo", client = "Copilot CLI" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-02T01:01:00Z",
                type = "user.message",
                payload = new { sessionId, messageId = "u1", content = "請整理登入流程" }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-02T01:02:00Z",
                type = "assistant.message",
                payload = new { sessionId, messageId = "a1", content = "已完成整理" }
            })
        ]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal(EvidenceKind.CopilotSession, evidence.Kind);
        Assert.Equal("copilot-session", evidence.RepositoryKey);
        Assert.Contains(sessionId, evidence.ExternalKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("請整理登入流程", evidence.Title);
        Assert.Equal("請整理登入流程", evidence.CommitMessage);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson);
        Assert.NotNull(metadata);
        Assert.Equal("Copilot CLI", metadata.Client);
        Assert.Equal(["user", "assistant"], metadata.Messages.Select(message => message.Role));
        Assert.Equal(@"C:\Work\Repo", metadata.Cwd);
    }

    [Fact]
    public async Task Malformed_jsonl_keeps_readable_messages_and_retries_file()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionDirectory = Path.Combine(testRoot, "session-state", sessionId);
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, "events.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            "{ malformed",
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-09-02T01:00:00Z",
                type = "USER_PROMPT",
                data = new { session_id = sessionId, content = "保留並重試" }
            })
        ]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.False(SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)!.IsComplete);
        Assert.Contains("重試", batch.Warnings.Single(), StringComparison.Ordinal);
        Assert.Contains("events.jsonl", batch.CheckpointJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_without_user_text_is_skipped()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionDirectory = Path.Combine(testRoot, "session-state", sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "events.jsonl"), JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-02T01:00:00Z",
            type = "assistant_reply",
            payload = new { sessionId, content = "只有助理回覆" }
        }));

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        Assert.Empty(batch.Evidence);
    }

    [Fact]
    public async Task Session_without_message_or_creation_time_is_skipped_with_warning()
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var sessionDirectory = Path.Combine(testRoot, "session-state", sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "events.jsonl"), JsonSerializer.Serialize(new
        {
            type = "user.message",
            payload = new { sessionId, content = "沒有時間的提示詞" }
        }));

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        Assert.Empty(batch.Evidence);
        Assert.Contains(batch.Warnings, warning => warning.Contains("時間", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wsl_home_resolver_uses_selected_distro()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(
            0,
            @"\\wsl.localhost\Ubuntu\home\me\.copilot" + Environment.NewLine,
            string.Empty));
        var resolver = new CopilotHomeResolver(runner);
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WslCopilot,
            SettingsJson = SourceSettingsSerializer.Serialize(new CopilotSourceSettings { Distro = "Ubuntu" })
        };

        var result = await resolver.ResolveAsync(ActivitySourceType.WslCopilot, source, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\.copilot", result.Path);
        Assert.Contains("Ubuntu", runner.LastRequest!.Arguments);
    }

    private CopilotSourceAdapter CreateAdapter() => new(new TestHomeResolver(testRoot), ActivitySourceType.WindowsCopilot);

    private ActivitySource CreateSource() => new()
    {
        SourceType = ActivitySourceType.WindowsCopilot,
        Enabled = true,
        SettingsJson = SourceSettingsSerializer.Serialize(new CopilotSourceSettings { CopilotHome = testRoot })
    };

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class TestHomeResolver(string path) : ICopilotHomeResolver
    {
        public Task<CopilotHomeResolution> ResolveAsync(
            ActivitySourceType sourceType,
            ActivitySource source,
            CancellationToken cancellationToken) =>
            Task.FromResult(CopilotHomeResolution.Success(path));
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
