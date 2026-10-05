using System.Text.Json.Nodes;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class McpCliSetupTests
{
    private const string Url = "http://127.0.0.1:5077/mcp";

    [Fact]
    public async Task Codex_project_name_does_not_conflict_with_an_unconfigured_server()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".codex"));
            await File.WriteAllTextAsync(Path.Combine(directory, ".codex", "config.toml"),
                "[projects.'C:/projects/worklens']\ntrust_level = 'trusted'\n[mcp_servers.other]\nurl = 'http://127.0.0.1:1234/mcp'\n");
            Assert.Equal("未設定", await new McpCliSetup(directory, new FakeRunner()).GetStatusAsync("codex", Url));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Application_cli_command_exits_without_starting_web_host()
    {
        var result = await new ProcessRunner().RunAsync(new("dotnet",
            [typeof(McpCliSetup).Assembly.Location, "--mcp-command", "invalid"]), timeout: TimeSpan.FromSeconds(15));
        Assert.False(result.TimedOut);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("worklens mcp", result.StandardError);
        Assert.DoesNotContain("Now listening", result.StandardOutput);
    }

    [Fact]
    public async Task Antigravity_does_not_overwrite_configuration_added_during_detection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, ".gemini", "config", "mcp_config.json");
        const string original = "{\"mcpServers\":{\"worklens\":{\"serverUrl\":\"http://127.0.0.1:9999/mcp\"}}}";
        try
        {
            var runner = new FakeRunner(async _ =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, original);
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => new McpCliSetup(directory, runner).SetupAsync("antigravity", Url));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Missing_client_returns_manual_instructions_without_writing_config()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        var setup = new McpCliSetup(directory, new MissingRunner());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.SetupAsync("antigravity", Url));
        Assert.Contains("手動設定", exception.Message);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Antigravity_setup_merges_backs_up_and_is_repeatable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, ".gemini", "config", "mcp_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"otherSetting\":42,\"mcpServers\":{\"other\":{\"command\":\"node\"}}}");
            var runner = new FakeRunner();
            var setup = new McpCliSetup(directory, runner);
            await setup.SetupAsync("antigravity", Url);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal(42, root["otherSetting"]!.GetValue<int>());
            Assert.Equal("node", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
            Assert.Equal(Url, root["mcpServers"]!["worklens"]!["serverUrl"]!.GetValue<string>());
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak"));
            await setup.SetupAsync("antigravity", Url);
            Assert.Single(runner.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"mcpServers\":[]}")]
    [InlineData("{\"mcpServers\":{\"worklens\":{\"serverUrl\":\"http://127.0.0.1:9999/mcp\"}}}")]
    public async Task Malformed_or_conflicting_config_is_never_overwritten(string original)
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, ".gemini", "config", "mcp_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, original);
            var setup = new McpCliSetup(directory, new FakeRunner());
            await Assert.ThrowsAnyAsync<Exception>(() => setup.SetupAsync("antigravity", Url));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    public async Task Disabled_configuration_is_preserved(string client)
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".copilot"));
            var path = client == "claude" ? Path.Combine(directory, ".claude.json") : Path.Combine(directory, ".copilot", "mcp-config.json");
            await File.WriteAllTextAsync(path, "{\"mcpServers\":{\"worklens\":{\"url\":\"" + Url + "\",\"disabled\":true}}}");
            var runner = new FakeRunner();
            Assert.Contains("停用", await new McpCliSetup(directory, runner).SetupAsync(client, Url));
            Assert.Empty(runner.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("copilot")]
    public async Task Setup_uses_official_command_and_checks_configuration(string client)
    {
        var directory = Path.Combine(Path.GetTempPath(), "worklens-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new FakeRunner(async request =>
            {
                if (request.Arguments[0] == "--version") return;
                var path = client switch { "codex" => Path.Combine(directory, ".codex", "config.toml"), "claude" => Path.Combine(directory, ".claude.json"), _ => Path.Combine(directory, ".copilot", "mcp-config.json") };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, client == "codex" ? "[mcp_servers.worklens]\nurl = \"" + Url + "\"\n" : "{\"mcpServers\":{\"worklens\":{\"url\":\"" + Url + "\"}}}");
            });
            var setup = new McpCliSetup(directory, runner);
            await setup.SetupAsync(client, Url);
            Assert.Equal(2, runner.Calls.Count);
            Assert.Contains(Url, runner.Calls[1].Arguments);
            await setup.SetupAsync(client, Url);
            Assert.Equal(2, runner.Calls.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class MissingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(-1, "", "unavailable"));
    }

    private sealed class FakeRunner(Func<ProcessRequest, Task>? callback = null) : IProcessRunner
    {
        public List<ProcessRequest> Calls { get; } = [];
        public async Task<ProcessResult> RunAsync(ProcessRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            if (callback is not null) await callback(request);
            return new(0, "ok", "");
        }
    }
}
