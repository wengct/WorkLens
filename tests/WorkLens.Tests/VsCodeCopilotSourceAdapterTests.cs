using System.Text.Json;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class VsCodeCopilotSourceAdapterTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "WorkLens.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Json_snapshot_imports_copilot_chat_and_excludes_other_responder()
    {
        var workspace = CreateWorkspace("stable-workspace");
        await File.WriteAllTextAsync(Path.Combine(workspace, "chatSessions", "copilot.json"), JsonSerializer.Serialize(new
        {
            sessionId = "copilot-session",
            responderUsername = "github.copilot",
            creationDate = "2026-09-02T02:00:00Z",
            workingDirectory = @"C:\Repo With Space",
            requests = new[]
            {
                new
                {
                    requestId = "request-1",
                    timestamp = "2026-09-02T02:01:00Z",
                    message = new { text = "請檢查測試" },
                    response = new object[]
                    {
                        new { value = "測試已檢查" },
                        new { kind = "thinking", value = "不應保存的推理" },
                        new { kind = "toolInvocationSerialized", invocationMessage = "不應保存的工具輸出" }
                    }
                }
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(workspace, "chatSessions", "other.json"), JsonSerializer.Serialize(new
        {
            sessionId = "other-session",
            responderUsername = "other-assistant",
            requests = new[] { new { message = "不應匯入" } }
        }));

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal("請檢查測試", evidence.Title);
        Assert.Equal(@"C:\Repo With Space", evidence.RepositoryPath);
        Assert.Equal("請檢查測試", evidence.CommitMessage);
        Assert.Contains("vscode-chat-session-json", evidence.ExternalKey, StringComparison.Ordinal);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)!;
        Assert.Equal(["user", "assistant"], metadata.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task Jsonl_operations_replay_initial_value_and_append_request()
    {
        var workspace = CreateWorkspace("jsonl-workspace");
        var path = Path.Combine(workspace, "chatSessions", "session.jsonl");
        var initial = new
        {
            sessionId = "jsonl-session",
            responderUsername = "copilot",
            creationDate = "2026-09-02T03:00:00Z",
            requests = Array.Empty<object>()
        };
        var request = new
        {
            requestId = "r1",
            timestamp = "2026-09-02T03:01:00Z",
            message = "追加的問題",
            response = new[] { new { kind = "markdown", value = "追加的回覆" } }
        };
        await File.WriteAllLinesAsync(path,
        [
            JsonSerializer.Serialize(new { kind = 0, v = initial }),
            JsonSerializer.Serialize(new { kind = 2, k = new[] { "requests" }, v = request })
        ]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal("追加的問題", evidence.CommitMessage);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)!;
        Assert.Contains(metadata.Messages, message => message.Text == "追加的回覆");
        Assert.Contains("vscode-chat-session-jsonl", evidence.ExternalKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_session_without_copilot_identity_is_skipped()
    {
        var workspace = CreateWorkspace("unknown-workspace");
        await File.WriteAllTextAsync(Path.Combine(workspace, "chatSessions", "unknown.json"), JsonSerializer.Serialize(new
        {
            sessionId = "unknown",
            requests = new[] { new { message = "沒有明確來源" } }
        }));

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        Assert.Empty(batch.Evidence);
        Assert.Contains(batch.Warnings, warning => warning.Contains("Copilot 識別", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chat_session_without_message_or_creation_time_is_skipped_with_warning()
    {
        var workspace = CreateWorkspace("missing-time-workspace");
        await File.WriteAllTextAsync(Path.Combine(workspace, "chatSessions", "missing-time.json"), JsonSerializer.Serialize(new
        {
            sessionId = "missing-time",
            responderUsername = "GitHub Copilot",
            requests = new[] { new { message = new { text = "沒有時間的提示詞" } } }
        }));

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        Assert.Empty(batch.Evidence);
        Assert.Contains(batch.Warnings, warning => warning.Contains("時間", StringComparison.Ordinal));
    }

    private string CreateWorkspace(string id)
    {
        var workspace = Path.Combine(testRoot, "workspaceStorage", id);
        Directory.CreateDirectory(Path.Combine(workspace, "chatSessions"));
        return workspace;
    }

    private VsCodeCopilotSourceAdapter CreateAdapter() =>
        new(new TestStorageResolver(Path.Combine(testRoot, "workspaceStorage")), ActivitySourceType.WindowsVsCodeCopilot);

    private ActivitySource CreateSource() => new()
    {
        SourceType = ActivitySourceType.WindowsVsCodeCopilot,
        Enabled = true,
        SettingsJson = SourceSettingsSerializer.Serialize(new VsCodeCopilotSourceSettings())
    };

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class TestStorageResolver(string path) : IVsCodeCopilotStorageResolver
    {
        public Task<VsCodeStorageResolution> ResolveAsync(
            ActivitySourceType sourceType,
            ActivitySource source,
            CancellationToken cancellationToken) =>
            Task.FromResult(VsCodeStorageResolution.Success([path]));
    }
}
