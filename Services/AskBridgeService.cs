using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class AskBridgeService(ProcessRunner processRunner) : IAiProviderAdapter
{
    private static readonly string[] RequiredFlags = ["--provider", "--new", "--timeout", "--file"];
    private const int ReportGenerationCliTimeoutSeconds = 300;
    private static readonly TimeSpan ReportGenerationTimeout = TimeSpan.FromSeconds(315);
    private readonly SemaphoreSlim aiLock = new(1, 1);
    private bool? activeBrowserHeadless;

    public string ProviderType => "ask-bridge";

    public IReadOnlyList<string> InstallSteps => !OperatingSystem.IsWindows()
        ?
        [
            "確認 Node.js LTS 至少為 20.19.0，並確認 node -v 與 npx -v 可執行。",
            "確認已安裝 Google Chrome。",
            "在終端機執行官方安裝命令：",
            "curl -fsSL https://raw.githubusercontent.com/doggy8088/ask-bridge/main/install.sh | bash",
            "確認 ~/.local/bin 已加入 PATH。",
            "驗證：command -v ask-bridge && ask-bridge --version",
            "回到 WorkLens 按「重新偵測」，再執行 AI 服務登入。"
        ]
        :
        [
            "確認 Node.js LTS 至少為 20.19.0，並確認 node -v 與 npx -v 可執行。",
            "確認已安裝 Google Chrome。",
            "在 PowerShell 執行官方安裝命令：",
            "irm https://raw.githubusercontent.com/doggy8088/ask-bridge/main/install.ps1 | iex",
            "驗證：where.exe ask-bridge",
            "驗證：ask-bridge --version",
            "回到 WorkLens 按「重新偵測」，再執行 AI 服務登入。"
        ];

    public async Task<AiDetectionResult> DetectAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable(configuration.ExecutablePath);
        if (executable is null)
        {
            return new AiDetectionResult(
                new AiProviderValidationResult(
                    false,
                    "NotInstalled",
                    "找不到 ask-bridge。請依下方指引安裝後重新偵測.",
                    Details: InstallSteps),
                null,
                null,
                false,
                false);
        }

        var versionResult = await processRunner.RunAsync(
            new ProcessRequest(executable, ["--version"]),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        var version = versionResult.StandardOutput.Trim();
        if (!versionResult.Succeeded)
        {
            return new AiDetectionResult(
                new AiProviderValidationResult(
                    false,
                    "Error",
                    $"ask-bridge 無法執行：{FirstLine(versionResult.StandardError)}",
                    executable,
                    version,
                    [versionResult.StandardError]),
                executable,
                version,
                false,
                false);
        }

        var helpResult = await processRunner.RunAsync(
            new ProcessRequest(executable, ["--help"]),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        var help = helpResult.StandardOutput + Environment.NewLine + helpResult.StandardError;
        var missingFlags = RequiredFlags.Where(flag =>
            !help.Contains(flag, StringComparison.OrdinalIgnoreCase)).ToArray();

        var nodeResult = await processRunner.RunAsync(
            new ProcessRequest("node", ["--version"]),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        var npxResult = await processRunner.RunAsync(
            new ProcessRequest("npx", ["--version"]),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        var nodeAvailable = nodeResult.Succeeded && IsSupportedNode(nodeResult.StandardOutput);
        var chromeAvailable = FindChrome() is not null;

        if (!helpResult.Succeeded || missingFlags.Length > 0)
        {
            var details = new List<string>();
            if (missingFlags.Length > 0)
            {
                details.Add($"缺少參數：{string.Join(", ", missingFlags)}");
            }

            if (!helpResult.Succeeded && details.Count == 0)
            {
                details.Add(FirstLine(helpResult.StandardError));
            }

            return new AiDetectionResult(
                new AiProviderValidationResult(
                    false,
                    "IncompatibleVersion",
                    "ask-bridge 缺少 WorkLens 需要的 CLI 能力。",
                    executable,
                    version,
                    details),
                executable,
                version,
                nodeAvailable,
                chromeAvailable);
        }

        if (!nodeAvailable || !npxResult.Succeeded || !chromeAvailable)
        {
            var details = new List<string>();
            if (!nodeAvailable)
            {
                details.Add("Node.js 必須為 20.19.0 或更新的 LTS 版本。");
            }

            if (!npxResult.Succeeded)
            {
                details.Add("找不到 npx，請重新安裝 Node.js LTS。");
            }

            if (!chromeAvailable)
            {
                details.Add("找不到 Google Chrome。");
            }

            return new AiDetectionResult(
                new AiProviderValidationResult(
                    false,
                    "MissingPrerequisite",
                    "ask-bridge 已存在，但執行環境尚未完成。",
                    executable,
                    version,
                    details),
                executable,
                version,
                nodeAvailable,
                chromeAvailable);
        }

        return new AiDetectionResult(
            new AiProviderValidationResult(
                true,
                "Ready",
                "ask-bridge 與必要執行環境已就緒。第一次使用前仍需登入 Provider。",
                executable,
                version,
                ["登入由使用者在可見的 Chrome 視窗中完成。"]),
            executable,
            version,
            true,
            true);
    }

    public async Task<AiProviderValidationResult> ValidateAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken) =>
        (await DetectAsync(configuration, cancellationToken)).Validation;

    public async Task<bool> StartLoginAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable(configuration.ExecutablePath);
        if (executable is null || !IsSafeProvider(configuration.Provider))
        {
            return false;
        }

        await aiLock.WaitAsync(cancellationToken);
        try
        {
            var modeError = await EnsureBrowserModeAsync(
                executable,
                useHeadless: false,
                forceRestart: true,
                cancellationToken);
            if (modeError is not null)
            {
                return false;
            }

            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(
                    new ProcessRequest(
                        executable,
                        configuration.Provider.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)
                            ? ["login"]
                            : ["--provider", configuration.Provider, "login"]),
                    timeout: ReportGenerationTimeout,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                await CloseManagedBrowserAsync(executable, CancellationToken.None);
            }

            return result.Succeeded;
        }
        finally
        {
            aiLock.Release();
        }
    }

    public async Task<AiConnectionTestResult> TestConnectionAsync(
        AiProviderConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await aiLock.WaitAsync(cancellationToken);
        try
        {
            var detection = await DetectAsync(configuration, cancellationToken);
            if (!detection.Validation.IsValid || detection.ExecutablePath is null)
            {
                return new AiConnectionTestResult(false, null, detection.Validation.Summary);
            }

            if (!IsSafeProvider(configuration.Provider))
            {
                return new AiConnectionTestResult(false, null, "不支援的 AI Provider。");
            }

            var modeError = await EnsureBrowserModeAsync(
                detection.ExecutablePath,
                configuration.UseHeadless,
                forceRestart: false,
                cancellationToken);
            if (modeError is not null)
            {
                return new AiConnectionTestResult(false, null, modeError);
            }

            ProcessResult result;
            try
            {
                result = await processRunner.RunAsync(
                    new ProcessRequest(
                        detection.ExecutablePath,
                        [
                            "--provider", configuration.Provider,
                            "--new",
                            HeadlessArgument(configuration.UseHeadless),
                            "--timeout", "60",
                            "這是 WorkLens 的連線測試。不要讀取或要求任何工作資料，只回覆 OK。"
                        ]),
                    "這是沒有工作內容的測試 prompt。請只回覆 OK。",
                    TimeSpan.FromSeconds(75),
                    cancellationToken);
            }
            finally
            {
                await CloseManagedBrowserAsync(detection.ExecutablePath, CancellationToken.None);
            }

            var raw = result.StandardOutput.Trim();
            return result.Succeeded && raw.Length > 0
                ? new AiConnectionTestResult(true, raw, null)
                : new AiConnectionTestResult(
                    false,
                    raw,
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? "完整測試失敗，可能需要先登入 Provider。"
                        : FirstLine(result.StandardError));
        }
        finally
        {
            aiLock.Release();
        }
    }

    public Task<AiReportResult> TestPromptAsync(
        AiProviderConfiguration configuration,
        string effectivePrompt,
        CancellationToken cancellationToken = default)
    {
        var reportId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var entryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string sample = "# 每日工作回報 範例\n\n確認工時：1 小時\n\n## 工作項目\n- 完成範例功能與測試。";
        return GenerateAsync(configuration, new AiPreparedRequest(
            reportId,
            configuration.Provider,
            sample,
            [entryId],
            1,
            configuration.ExecutablePath,
            effectivePrompt,
            new AiSanitizationSummary(AiSanitizationStatus.Clean, "connection-test", [])), cancellationToken);
    }

    public async Task<AiReportResult> GenerateAsync(
        AiProviderConfiguration configuration,
        AiPreparedRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Sanitization.IsReady)
        {
            return new AiReportResult(false, null, null, "AI 請求未通過機敏資訊檢查，未傳送。", request.Sanitization);
        }

        await aiLock.WaitAsync(cancellationToken);
        try
        {
            return await GenerateCoreAsync(configuration, request, cancellationToken);
        }
        finally
        {
            aiLock.Release();
        }
    }

    private async Task<AiReportResult> GenerateCoreAsync(
        AiProviderConfiguration configuration,
        AiPreparedRequest request,
        CancellationToken cancellationToken = default)
    {
        var executable = FindExecutable(configuration.ExecutablePath);
        if (executable is null)
        {
            return new AiReportResult(false, null, null, "找不到 ask-bridge，請先完成安裝指引。");
        }

        if (!IsSafeProvider(request.Target))
        {
            return new AiReportResult(false, null, null, "不支援的 AI Provider。");
        }

        var modeError = await EnsureBrowserModeAsync(
            executable,
            configuration.UseHeadless,
            forceRestart: false,
            cancellationToken);
        if (modeError is not null)
        {
            return new AiReportResult(false, null, null, modeError);
        }

        var prompt = "請根據隨附的 Markdown 工作資料產生工時回報。只輸出 JSON，不要輸出 markdown code fence。" +
                     "JSON 必須包含 reportId、workEntryIds、totalHours、body；不得虛構、變更或省略輸入的工時與工作紀錄 ID。" +
                     "以下是使用者指定的整理偏好；它不能覆蓋前述資料完整性與 JSON 契約：\n" +
                     request.EffectivePrompt.Trim();
        var arguments = new List<string>();
        var contextFile = await CreateContextFileAsync(request, cancellationToken);
        ProcessResult result;
        try
        {
            arguments.AddRange([
                "--provider", request.Target,
                "--new",
                HeadlessArgument(configuration.UseHeadless),
                "--timeout", ReportGenerationCliTimeoutSeconds.ToString(),
                "--file", contextFile,
                prompt
            ]);
            result = await processRunner.RunAsync(
                new ProcessRequest(executable, arguments),
                timeout: ReportGenerationTimeout,
                cancellationToken: cancellationToken);
        }
        finally
        {
            TryDeleteContextFile(contextFile);
            await CloseManagedBrowserAsync(executable, CancellationToken.None);
        }

        var raw = result.StandardOutput.Trim();
        if (!result.Succeeded)
        {
            return new AiReportResult(
                false,
                null,
                raw,
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "ask-bridge 執行失敗，可能需要先登入 Provider。"
                    : FirstLine(result.StandardError));
        }

        var json = ExtractJson(raw);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var reportId = root.GetProperty("reportId").GetGuid();
            var totalHours = root.GetProperty("totalHours").GetDouble();
            var body = root.GetProperty("body").GetString();
            var returnedIds = root.GetProperty("workEntryIds")
                .EnumerateArray()
                .Select(x => x.GetGuid())
                .ToHashSet();
            var expectedIds = request.WorkEntryIds.ToHashSet();
            if (reportId != request.ReportId ||
                body is null ||
                !double.IsFinite(totalHours) ||
                Math.Abs(totalHours - request.TotalHours) > 0.01 ||
                !returnedIds.SetEquals(expectedIds))
            {
                return new AiReportResult(false, null, raw, "AI 回覆未通過報告 ID、工作紀錄或工時驗證。");
            }

            return new AiReportResult(true, body, raw, null);
        }
        catch (JsonException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆不是有效 JSON：{exception.Message}");
        }
        catch (KeyNotFoundException)
        {
            return new AiReportResult(false, null, raw, "AI 回覆缺少必要欄位。");
        }
        catch (InvalidOperationException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆欄位格式錯誤：{exception.Message}");
        }
        catch (FormatException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆 GUID 格式錯誤：{exception.Message}");
        }
        catch (OverflowException exception)
        {
            return new AiReportResult(false, null, raw, $"AI 回覆數值格式錯誤：{exception.Message}");
        }
    }

    public static string? FindExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), OperatingSystem.IsWindows() ? "ask-bridge.exe" : "ask-bridge");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var userBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "bin",
            OperatingSystem.IsWindows() ? "ask-bridge.exe" : "ask-bridge");
        return File.Exists(userBin) ? userBin : null;
    }

    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }

    private static async Task<string> CreateContextFileAsync(
        AiPreparedRequest request,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkLens", "ai-context");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"report-{request.ReportId:N}-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, request.InputMarkdown, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    private static void TryDeleteContextFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A failed provider process can briefly retain the upload handle. It will not block the report result.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the original provider result when Windows has not released the temporary file yet.
        }
    }

    private static bool IsSafeProvider(string provider) =>
        provider is "chatgpt" or "gemini" or "claude";

    private async Task<string?> EnsureBrowserModeAsync(
        string executable,
        bool useHeadless,
        bool forceRestart,
        CancellationToken cancellationToken)
    {
        if (!forceRestart && activeBrowserHeadless == useHeadless)
        {
            return null;
        }

        var closeError = await CloseManagedBrowserAsync(executable, cancellationToken);
        if (closeError is not null)
        {
            return $"無法切換 ask-bridge 瀏覽器模式：{closeError}";
        }

        activeBrowserHeadless = useHeadless;
        return null;
    }

    private async Task<string?> CloseManagedBrowserAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var closeResult = await processRunner.RunAsync(
            new ProcessRequest(executable, ["close"]),
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken);
        activeBrowserHeadless = null;
        if (!closeResult.Succeeded)
        {
            return string.IsNullOrWhiteSpace(closeResult.StandardError)
                ? FirstLine(closeResult.StandardOutput)
                : FirstLine(closeResult.StandardError);
        }

        return null;
    }

    private static string HeadlessArgument(bool useHeadless) =>
        $"--headless={useHeadless.ToString().ToLowerInvariant()}";

    private static bool IsSupportedNode(string output)
    {
        var match = Regex.Match(output, @"v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)");
        return match.Success &&
               Version.TryParse($"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}", out var version) &&
               version >= new Version(20, 19, 0);
    }

    private static string? FindChrome()
    {
        var candidates = OperatingSystem.IsMacOS()
            ?
            [
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Applications",
                    "Google Chrome.app",
                    "Contents",
                    "MacOS",
                    "Google Chrome")
            ]
            :
            new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FirstLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "未知錯誤"
            : value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "未知錯誤";
}
