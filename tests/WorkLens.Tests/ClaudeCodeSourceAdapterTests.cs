using System.Text.Json;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ClaudeCodeSourceAdapterTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "WorkLens.Tests",
        Guid.NewGuid().ToString("N"));

    public ClaudeCodeSourceAdapterTests()
    {
        Directory.CreateDirectory(Path.Combine(testRoot, "projects", "C--Work--Repo"));
        Directory.CreateDirectory(Path.Combine(testRoot, "projects", "C--Work--Repo", "subagents"));
    }

    [Fact]
    public async Task Collection_preserves_visible_messages_and_excludes_tools_and_subagents()
    {
        var id = Guid.NewGuid().ToString("D");
        await File.WriteAllLinesAsync(SessionPath(id),
        [
            Row("2026-09-02T01:00:00Z", "system", new { session_id = id, cwd = @"C:\Work\Repo", version = "1.0.9" }),
            Row("2026-09-02T01:01:00Z", "user", new
            {
                sessionId = id,
                uuid = "user-1",
                message = new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "請修正登入流程" },
                        new { type = "tool_result", content = "不要匯入這段工具輸出" }
                    }
                }
            }),
            Row("2026-09-02T01:02:00Z", "assistant", new
            {
                sessionId = id,
                uuid = "assistant-1",
                message = new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "text", text = "已完成修正" },
                        new { type = "tool_use", input = "不要匯入這段工具輸入" },
                        new { type = "thinking", thinking = "不要匯入思考" }
                    }
                }
            }),
            Row("2026-09-02T01:03:00Z", "user", new
            {
                sessionId = id,
                uuid = "tool-result",
                message = new
                {
                    role = "user",
                    content = new[] { new { type = "tool_result", content = "工具結果" } }
                }
            })
        ]);
        await File.WriteAllLinesAsync(Path.Combine(testRoot, "projects", "C--Work--Repo", "subagents", "child.jsonl"),
        [Row("2026-09-02T01:04:00Z", "user", new { sessionId = Guid.NewGuid(), message = "子代理不應匯入" })]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal(EvidenceKind.ClaudeCodeSession, evidence.Kind);
        Assert.Equal("claude-code-session", evidence.RepositoryKey);
        Assert.Equal($"session:{id}", evidence.ExternalKey);
        Assert.Equal("請修正登入流程", evidence.Title);
        Assert.Equal("請修正登入流程", evidence.CommitMessage);
        Assert.DoesNotContain("已完成修正", evidence.CommitMessage);
        Assert.DoesNotContain("工具輸出", evidence.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("工具輸入", evidence.MetadataJson, StringComparison.Ordinal);
        var metadata = SourceSettingsSerializer.DeserializeClaudeCodeMetadata(evidence.MetadataJson);
        Assert.NotNull(metadata);
        Assert.Equal(["user", "assistant"], metadata.Messages.Select(message => message.Role));
        Assert.Equal(@"C:\Work\Repo", metadata.Cwd);
        Assert.Equal("1.0.9", metadata.CliVersion);
    }

    [Fact]
    public async Task Malformed_lines_are_reported_and_checkpoint_keeps_file_for_retry()
    {
        var id = Guid.NewGuid().ToString("D");
        var path = SessionPath(id);
        await File.WriteAllLinesAsync(path,
        [
            "{ malformed",
            Row("2026-09-02T01:00:00Z", "user", new { sessionId = id, message = new { role = "user", content = "保留重試" } })
        ]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        Assert.Single(batch.Evidence);
        Assert.Contains(batch.Warnings, warning => warning.Contains("重試", StringComparison.Ordinal));
        using var checkpoint = JsonDocument.Parse(batch.CheckpointJson);
        Assert.Contains(
            checkpoint.RootElement.GetProperty("RetryPaths").EnumerateArray(),
            item => string.Equals(item.GetString(), path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_without_user_text_is_skipped()
    {
        var id = Guid.NewGuid().ToString("D");
        await File.WriteAllLinesAsync(SessionPath(id),
        [Row("2026-09-02T01:00:00Z", "assistant", new
        {
            sessionId = id,
            message = new { role = "assistant", content = new[] { new { type = "text", text = "只有回覆" } } }
        })]);

        var batch = await CreateAdapter().CollectAsync(
            new CollectionRequest(CreateSource(), DateTimeOffset.Parse("2026-09-01T00:00:00Z")),
            CancellationToken.None);

        Assert.Empty(batch.Evidence);
    }

    [Fact]
    public async Task Wsl_home_resolver_uses_claude_config_dir_and_requires_distro()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(
            0,
            @"\\wsl.localhost\Ubuntu\home\me\.claude" + Environment.NewLine,
            string.Empty));
        var resolver = new ClaudeCodeHomeResolver(runner);
        var source = new ActivitySource
        {
            SourceType = ActivitySourceType.WslClaudeCode,
            SettingsJson = SourceSettingsSerializer.Serialize(new ClaudeCodeSourceSettings { Distro = "Ubuntu" })
        };

        var result = await resolver.ResolveAsync(ActivitySourceType.WslClaudeCode, source, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(runner.LastRequest!.Arguments, argument => argument.Contains("CLAUDE_CONFIG_DIR", StringComparison.Ordinal));

        var missingDistro = await resolver.ResolveAsync(
            ActivitySourceType.WslClaudeCode,
            new ActivitySource
            {
                SourceType = ActivitySourceType.WslClaudeCode,
                SettingsJson = SourceSettingsSerializer.Serialize(new ClaudeCodeSourceSettings())
            },
            CancellationToken.None);
        Assert.False(missingDistro.Succeeded);
        Assert.Contains("Ubuntu", missingDistro.Error);
    }

    private ClaudeCodeSourceAdapter CreateAdapter() =>
        new(new TestHomeResolver(testRoot), LocalSourceType);

    private ActivitySource CreateSource() => new()
    {
        SourceType = LocalSourceType,
        Enabled = true,
        SettingsJson = SourceSettingsSerializer.Serialize(new ClaudeCodeSourceSettings { ClaudeCodeHome = testRoot })
    };

    private ActivitySourceType LocalSourceType =>
        OperatingSystem.IsMacOS() ? ActivitySourceType.MacOsClaudeCode : ActivitySourceType.WindowsClaudeCode;

    private string SessionPath(string id) => Path.Combine(
        testRoot,
        "projects",
        "C--Work--Repo",
        $"{id}.jsonl");

    private static string Row(string timestamp, string type, object payload) =>
        JsonSerializer.Serialize(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload))!
                .Concat(new[]
                {
                    new KeyValuePair<string, JsonElement>("timestamp", JsonSerializer.SerializeToElement(timestamp)),
                    new KeyValuePair<string, JsonElement>("type", JsonSerializer.SerializeToElement(type))
                })
                .ToDictionary(item => item.Key, item => item.Value));

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class TestHomeResolver(string path) : IClaudeCodeHomeResolver
    {
        public Task<ClaudeCodeHomeResolution> ResolveAsync(
            ActivitySourceType sourceType,
            ActivitySource source,
            CancellationToken cancellationToken) =>
            Task.FromResult(ClaudeCodeHomeResolution.Success(path));
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
