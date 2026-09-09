using System.Text;
using WorkLens.Services;

namespace WorkLens.Tests;

[Collection("Console encoding")]
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Chinese_standard_input_is_utf8_without_bom_on_a_big5_console()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string prompt = "這是沒有工作內容的測試 prompt。請只回覆 OK。";
        var originalEncoding = Console.InputEncoding;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            Console.InputEncoding = Encoding.GetEncoding(950);
            // Read the actual pipe bytes, independently of the child's console encoding.
            const string script = "$stream = [Console]::OpenStandardInput(); " +
                "$buffer = New-Object System.IO.MemoryStream; $stream.CopyTo($buffer); " +
                "[Console]::Write([Convert]::ToBase64String($buffer.ToArray()))";
            var result = await new ProcessRunner().RunAsync(
                new ProcessRequest("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]),
                prompt,
                TimeSpan.FromSeconds(15));

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal(Encoding.UTF8.GetBytes(prompt), Convert.FromBase64String(result.StandardOutput));
        }
        finally
        {
            Console.InputEncoding = originalEncoding;
        }
    }

    [Fact]
    public async Task Returns_after_parent_exits_when_a_descendant_keeps_the_output_pipe_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(Path.GetTempPath(), $"worklens-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var scriptPath = Path.Combine(testDirectory, "leaves-child-running.cmd");
        await File.WriteAllTextAsync(
            scriptPath,
            "@start \"\" /b cmd /c \"ping 127.0.0.1 -n 5 > nul\"\r\n@echo completed\r\n");

        try
        {
            var result = await new ProcessRunner().RunAsync(
                new ProcessRequest(scriptPath, []),
                timeout: TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Contains("completed", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Runs_windows_command_scripts_without_shell_execute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testDirectory = Path.Combine(Path.GetTempPath(), $"worklens-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var scriptPath = Path.Combine(testDirectory, "test-command.cmd");
        await File.WriteAllTextAsync(scriptPath, "@echo npx-compatible\r\n");

        try
        {
            var result = await new ProcessRunner().RunAsync(
                new ProcessRequest("test-command", ["--version"], testDirectory),
                timeout: TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded, result.StandardError);
            Assert.Contains("npx-compatible", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }
}

[CollectionDefinition("Console encoding", DisableParallelization = true)]
public sealed class ConsoleEncodingCollection;
