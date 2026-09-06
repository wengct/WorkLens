using System.Diagnostics;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AskBridgeServiceTests
{
    [Fact]
    public async Task Headless_generation_restarts_an_unknown_managed_browser_and_closes_it_after_invocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(
            Environment.CurrentDirectory,
            ".test-build",
            $"worklens-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var executable = Path.Combine(testDirectory, "ask-bridge.cmd");
        var invocationLog = Path.Combine(testDirectory, "invocations.txt");
        var reportId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        await File.WriteAllTextAsync(executable, $$"""
            @echo off
            echo %~1>>"{{invocationLog}}"
            if /I "%~1"=="close" exit /b 0
            echo ARGS=%*
            echo {"reportId":"{{reportId}}","workEntryIds":["{{entryId}}"],"totalHours":1,"body":"ok"}
            """);

        try
        {
            var result = await new AskBridgeService(new ProcessRunner()).GenerateAsync(
                new AiProviderConfiguration
                {
                    Provider = "chatgpt",
                    ExecutablePath = executable,
                    UseHeadless = true
                },
                PreparedRequest(
                    reportId,
                    "chatgpt",
                    "context",
                    [entryId],
                    1,
                    executable,
                    "請簡潔整理。"));

            Assert.True(result.Succeeded, result.Error);
            var invocations = await File.ReadAllLinesAsync(invocationLog);
            Assert.Equal(["close", "--provider", "close"], invocations);
            Assert.Contains("--headless=true", result.RawResponse, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Login_closes_the_managed_browser_after_completion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(
            Environment.CurrentDirectory,
            ".test-build",
            $"worklens-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var executable = Path.Combine(testDirectory, "ask-bridge.cmd");
        var invocationLog = Path.Combine(testDirectory, "invocations.txt");
        await File.WriteAllTextAsync(executable, $$"""
            @echo off
            echo %~1>>"{{invocationLog}}"
            exit /b 0
            """);

        try
        {
            var succeeded = await new AskBridgeService(new ProcessRunner()).StartLoginAsync(
                new AiProviderConfiguration
                {
                    Provider = "chatgpt",
                    ExecutablePath = executable
                });

            Assert.True(succeeded);
            Assert.Equal(["close", "login", "close"], await File.ReadAllLinesAsync(invocationLog));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Report_generation_uploads_large_context_as_a_complete_temporary_markdown_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(Path.GetTempPath(), $"worklens-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var executable = Path.Combine(testDirectory, "ask-bridge.cmd");
        await File.WriteAllTextAsync(executable, """
            @echo off
            echo ARGS=%*
            :loop
            if "%~1"=="" goto done
            if /I "%~1"=="--file" (
              echo CONTEXT_FILE=%~2
              for %%A in ("%~2") do echo CONTEXT_BYTES=%%~zA
              goto done
            )
            shift
            goto loop
            :done
            """);

        try
        {
            var input = new string('x', 128 * 1024) + Environment.NewLine + "END-OF-CONTEXT";
            var result = await new AskBridgeService(new ProcessRunner()).GenerateAsync(
                new AiProviderConfiguration
                {
                    Provider = "chatgpt",
                    ExecutablePath = executable,
                    UseHeadless = false
                },
                PreparedRequest(
                    Guid.NewGuid(),
                    "chatgpt",
                    input,
                    [Guid.NewGuid()],
                    1,
                    executable,
                    "請簡潔整理。"));

            Assert.Contains("--timeout 300", result.RawResponse, StringComparison.Ordinal);
            Assert.Contains("--headless=false", result.RawResponse, StringComparison.Ordinal);
            Assert.Contains("--file", result.RawResponse, StringComparison.Ordinal);
            Assert.Contains($"CONTEXT_BYTES={input.Length}", result.RawResponse, StringComparison.Ordinal);

            var contextFileLine = result.RawResponse!
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("CONTEXT_FILE=", StringComparison.Ordinal));
            var contextFile = contextFileLine["CONTEXT_FILE=".Length..];
            Assert.EndsWith(".md", contextFile, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(contextFile));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Report_generation_attachment_contains_only_the_prepared_masked_context()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(Path.GetTempPath(), $"worklens-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var executable = Path.Combine(testDirectory, "ask-bridge.cmd");
        await File.WriteAllTextAsync(executable, """
            @echo off
            if /I "%~1"=="close" exit /b 0
            :loop
            if "%~1"=="" goto done
            if /I "%~1"=="--file" (
              type "%~2"
              goto done
            )
            shift
            goto loop
            :done
            echo {"reportId":"11111111-1111-1111-1111-111111111111","workEntryIds":["22222222-2222-2222-2222-222222222222"],"totalHours":1,"body":"ok"}
            """);

        try
        {
            const string marker = "[已遮蔽：機敏憑證]";
            var result = await new AskBridgeService(new ProcessRunner()).GenerateAsync(
                new AiProviderConfiguration
                {
                    Provider = "chatgpt",
                    ExecutablePath = executable,
                    UseHeadless = false
                },
                PreparedRequest(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "chatgpt",
                    $"工作資料：{marker}",
                    [Guid.Parse("22222222-2222-2222-2222-222222222222")],
                    1,
                    executable,
                    $"請整理：{marker}"));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains(marker, result.RawResponse, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static AiPreparedRequest PreparedRequest(
        Guid reportId,
        string target,
        string input,
        IReadOnlyList<Guid> entryIds,
        double totalHours,
        string? executablePath,
        string prompt) => new(
        reportId,
        target,
        input,
        entryIds,
        totalHours,
        executablePath,
        prompt,
        new AiSanitizationSummary(AiSanitizationStatus.Clean, "test", []));
}
