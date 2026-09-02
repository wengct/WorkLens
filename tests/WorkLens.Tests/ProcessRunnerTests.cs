using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class ProcessRunnerTests
{
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
                timeout: TimeSpan.FromMilliseconds(300));

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
