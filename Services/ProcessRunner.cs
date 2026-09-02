using System.Diagnostics;
using System.Text;

namespace WorkLens.Services;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan StreamDrainGracePeriod = TimeSpan.FromMilliseconds(250);

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? Environment.CurrentDirectory
            : request.WorkingDirectory;
        var executable = ResolveExecutable(request.FileName, request.WorkingDirectory);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "Process could not be started.");
            }

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput);
                process.StandardInput.Close();
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout.HasValue)
            {
                timeoutSource.CancelAfter(timeout.Value);
            }

            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);
            var outputTask = ReadStreamAsync(process.StandardOutput, standardOutput, streamCancellation.Token);
            var errorTask = ReadStreamAsync(process.StandardError, standardError, streamCancellation.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            var streamsCompleted = Task.WhenAll(outputTask, errorTask);
            if (await Task.WhenAny(streamsCompleted, Task.Delay(StreamDrainGracePeriod)) != streamsCompleted)
            {
                streamCancellation.Cancel();
            }

            await streamsCompleted;

            return new ProcessResult(
                process.ExitCode,
                standardOutput.ToString(),
                standardError.ToString());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(-1, string.Empty, "Process timed out or was cancelled.", TimedOut: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    private static string ResolveExecutable(string fileName, string? workingDirectory)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(fileName))
        {
            return fileName;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            directories.Add(workingDirectory);
        }

        directories.AddRange(
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => directory.Trim('"')));

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, fileName + extension.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
                {
                    // Ignore malformed PATH entries and continue with the remaining directories.
                }
            }
        }

        return fileName;
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        StringBuilder destination,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0)
                {
                    return;
                }

                destination.Append(buffer, 0, count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The direct child process has exited; a descendant kept this pipe open.
            // Return everything received so far instead of blocking the caller indefinitely.
        }
    }

    public bool StartDetached(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
                Arguments = string.Join(' ', arguments.Select(QuoteArgument))
            };
            return Process.Start(startInfo) is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"'))
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        return value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}
