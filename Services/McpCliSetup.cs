using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkLens.Services;

// This command runs before web hosting starts. It does not load the WorkLens database.
public sealed class McpCliSetup(string userDirectory, IProcessRunner runner)
{
    public static async Task<int> RunCommandAsync(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[1] is not ("setup" or "status"))
                throw new ArgumentException("用法：worklens mcp setup [codex|claude|antigravity|copilot] 或 worklens mcp status");
            var urlIndex = Array.IndexOf(args, "--url");
            var url = urlIndex >= 0 && args.Length > urlIndex + 1 ? args[urlIndex + 1] : "http://127.0.0.1:5077/mcp";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || !endpoint.IsLoopback || endpoint.AbsolutePath != "/mcp" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                throw new ArgumentException("MCP URL 必須是本機 http 位址且以 /mcp 結尾。");
            var setup = new McpCliSetup(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), new McpClientProcessRunner());
            var available = await ProbeAsync(endpoint);
            Console.WriteLine(available ? "WorkLens MCP：服務端工具探索成功（尚非客户端驗收）。" : "WorkLens MCP：無法探索，請啟動 WorkLens 並確認 MCP 未停用。");
            if (args[1] == "status")
            {
                foreach (var client in new[] { "codex", "claude", "antigravity", "copilot" })
                {
                    try { Console.WriteLine($"{client}：{await setup.GetStatusAsync(client, url)}"); }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
                    { Console.WriteLine($"{client}：無法讀取設定，請在客户端檢查。"); }
                }
                return available ? 0 : 1;
            }
            if (args.Length < 3) throw new ArgumentException("請指定客户端。");
            if (!available) return 1;
            Console.WriteLine(await setup.SetupAsync(args[2], url));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(exception is JsonException ? "設定 JSON 格式錯誤，未覆寫。" : exception.Message);
            return 1;
        }
    }

    public async Task<string> GetStatusAsync(string client, string url)
    {
        ValidateClient(client);
        if (client == "codex")
        {
            var config = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(userDirectory, ".codex"), "config.toml");
            if (!File.Exists(config)) return "未設定";
            var text = await File.ReadAllTextAsync(config);
            var section = Regex.Match(text, @"(?ms)^[ \t]*\[[ \t]*mcp_servers[ \t]*\.[ \t]*(?:worklens|""worklens""|'worklens')[ \t]*\][ \t\r]*(?:#[^\n]*)?$(.*?)(?=^[ \t]*\[|\z)");
            if (!section.Success)
            {
                var parent = Regex.Match(text, @"(?ms)^[ \t]*\[mcp_servers\][ \t\r]*(?:#[^\n]*)?$(.*?)(?=^[ \t]*\[|\z)");
                var inlineEntry = Regex.IsMatch(parent.Groups[1].Value, @"(?m)^[ \t]*(?:worklens|""worklens""|'worklens')[ \t]*=");
                var dottedEntry = Regex.IsMatch(text, @"(?m)^[ \t]*(?:\[[ \t]*)?mcp_servers[ \t]*\.[ \t]*(?:worklens\b|""worklens""|'worklens')");
                var rootInlineEntry = Regex.IsMatch(text, @"(?m)^[ \t]*mcp_servers[ \t]*=[ \t]*\{[^\r\n]*(?:\bworklens|""worklens""|'worklens')[ \t]*=");
                return inlineEntry || dottedEntry || rootInlineEntry
                    ? "同名設定衝突" : "未設定";
            }
            var value = Regex.Match(section.Groups[1].Value, @"(?m)^\s*url\s*=\s*[""']([^""']+)[""']");
            if (!value.Success || !SameUrl(value.Groups[1].Value, url)) return "同名設定衝突";
            return Regex.IsMatch(section.Groups[1].Value, @"(?m)^\s*enabled\s*=\s*false\b") ? "已設定但停用" : "已設定";
        }
        var root = await ReadConfigurationAsync(client);
        var servers = root["mcpServers"] as JsonObject;
        if (root.ContainsKey("mcpServers") && servers is null) throw new InvalidOperationException("mcpServers 格式錯誤，未覆寫。");
        var entry = servers?["worklens"];
        if (entry is null) return servers?.ContainsKey("worklens") == true ? "同名設定衝突" : "未設定";
        if (entry is not JsonObject definition) return "同名設定衝突";
        var valueNode = definition[client == "antigravity" ? "serverUrl" : "url"];
        if (!SameUrl(valueNode?.GetValue<string>(), url)) return "同名設定衝突";
        if (definition["disabled"]?.GetValue<bool>() == true || definition["enabled"]?.GetValue<bool>() == false) return "已設定但停用";
        if (client == "copilot")
        {
            var preferences = Path.Combine(userDirectory, ".copilot", "config.json");
            if (File.Exists(preferences) && JsonNode.Parse(await File.ReadAllTextAsync(preferences))?["disabledMcpServers"] is JsonArray disabled && disabled.Any(x => x?.GetValue<string>() == "worklens"))
                return "已設定但停用";
        }
        return "已設定";
    }

    public async Task<string> SetupAsync(string client, string url)
    {
        var status = await GetStatusAsync(client, url);
        if (status == "同名設定衝突") throw new InvalidOperationException("已有不同的 worklens 設定，未覆寫。請先用客户端管理工具檢查。");
        if (status != "未設定") return status + "；保留既有設定。";
        var command = client == "antigravity" ? "agy" : client;
        var version = await runner.RunAsync(new(command, ["--version"]), timeout: TimeSpan.FromSeconds(10));
        if (!version.Succeeded) throw new InvalidOperationException($"找不到或無法執行 {command}，請安裝客户端後再試。手動設定：{ManualInstruction(client, url)}");
        if (client == "antigravity")
        {
            var path = ConfigurationPath(client);
            var original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            var root = original is null ? new JsonObject() : JsonNode.Parse(original) as JsonObject
                ?? throw new InvalidOperationException("設定根節點必須是 JSON object，未覆寫。");
            var servers = root["mcpServers"] as JsonObject;
            if (root.ContainsKey("mcpServers") && servers is null)
                throw new InvalidOperationException("mcpServers 格式錯誤，未覆寫。");
            if (servers?.ContainsKey("worklens") == true)
                throw new InvalidOperationException("設定已被其他程序修改，未覆寫。");
            if (servers is null) { servers = new JsonObject(); root["mcpServers"] = servers; }
            servers["worklens"] = new JsonObject { ["serverUrl"] = url };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (original is not null) await File.WriteAllTextAsync(path + "." + Guid.NewGuid().ToString("N") + ".bak", original);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(path) ? await File.ReadAllTextAsync(path) != original : original is not null)
                    throw new InvalidOperationException("設定已被其他程序修改，未覆寫。");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else
        {
            string[] arguments = client switch
            {
                "codex" => ["mcp", "add", "worklens", "--url", url],
                "claude" => ["mcp", "add", "--scope", "user", "--transport", "http", "worklens", url],
                _ => ["mcp", "add", "--transport", "http", "worklens", url]
            };
            var result = await runner.RunAsync(new(command, arguments), timeout: TimeSpan.FromSeconds(20));
            if (!result.Succeeded) throw new InvalidOperationException($"客户端不支援或無法完成設定，請手動操作：{ManualInstruction(client, url)}");
        }
        if (await GetStatusAsync(client, url) != "已設定") throw new InvalidOperationException("設定命令完成，但無法確認使用者層級設定。請在客户端 /mcp 檢查。");
        return "WorkLens 已加入。請重新載入客户端並以 /mcp 驗證；未自動呼叫寫入工具。";
    }

    private async Task<JsonObject> ReadConfigurationAsync(string client)
    {
        var path = ConfigurationPath(client);
        if (!File.Exists(path)) return new();
        return JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject ?? throw new InvalidOperationException("設定根節點必須是 JSON object，未覆寫。");
    }

    private string ConfigurationPath(string client) => client switch
    {
        "antigravity" => Path.Combine(userDirectory, ".gemini", "config", "mcp_config.json"),
        "claude" => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } directory ? Path.Combine(directory, ".claude.json") : Path.Combine(userDirectory, ".claude.json"),
        "copilot" => Path.Combine(userDirectory, ".copilot", "mcp-config.json"),
        _ => throw new ArgumentException("不支援的客户端。")
    };

    private static void ValidateClient(string client)
    {
        if (client is not ("codex" or "claude" or "antigravity" or "copilot")) throw new ArgumentException("客户端須為 codex、claude、antigravity 或 copilot。");
    }

    private static bool SameUrl(string? left, string right) => Uri.TryCreate(left, UriKind.Absolute, out var first)
        && Uri.TryCreate(right, UriKind.Absolute, out var second) && first == second;

    private static string ManualInstruction(string client, string url) => client switch
    {
        "codex" => $"codex mcp add worklens --url {url}",
        "claude" => $"claude mcp add --scope user --transport http worklens {url}",
        "copilot" => $"copilot mcp add --transport http worklens {url}",
        _ => $"~/.gemini/config/mcp_config.json 的 mcpServers.worklens.serverUrl = {url}"
    };

    public static async Task<bool> ProbeAsync(Uri endpoint)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
            using var response = await http.PostAsync(endpoint, new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"worklens-setup\",\"version\":\"1\"}}}", System.Text.Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode) return false;
            http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
            using var tools = await http.PostAsync(endpoint, new StringContent("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", System.Text.Encoding.UTF8, "application/json"));
            var text = await tools.Content.ReadAsStringAsync();
            return tools.IsSuccessStatusCode && new[] { "worklens_get_daily_context", "worklens_create_work_entry", "worklens_save_daily_summary" }.All(text.Contains);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
    }
}

// npm installs expose .ps1/.cmd shims on Windows. Invoke through PowerShell with
// literal arguments, rather than attempting CreateProcess on a batch file.
public sealed class McpClientProcessRunner : IProcessRunner
{
    private readonly ProcessRunner runner = new();

    public Task<ProcessResult> RunAsync(ProcessRequest request, string? standardInput = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return runner.RunAsync(request, standardInput, timeout, cancellationToken);
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & "
            + Literal(request.FileName) + " " + string.Join(" ", request.Arguments.Select(Literal))
            + "; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return runner.RunAsync(new("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], request.WorkingDirectory), standardInput, timeout, cancellationToken);
    }
}
