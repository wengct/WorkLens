using System.Diagnostics;
using System.Runtime.Versioning;

namespace WorkLens.Tests;

public sealed class BackgroundLauncherTests
{
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Default_nvm_node_directory_is_added_to_the_child_path()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = FindRepositoryRoot();
        var testDirectory = Directory.CreateTempSubdirectory("worklens-launcher-");
        try
        {
            var installDirectory = testDirectory.FullName;
            var versionDirectory = Directory.CreateDirectory(Path.Combine(installDirectory, "versions", "test"));
            var nvmDirectory = Directory.CreateDirectory(Path.Combine(installDirectory, "nvm"));
            var nodeDirectory = Directory.CreateDirectory(Path.Combine(nvmDirectory.FullName, "versions", "node", "v22.0.0", "bin"));
            var pathOutput = Path.Combine(installDirectory, "child-path.txt");
            var executable = Path.Combine(versionDirectory.FullName, "WorkLens");
            var node = Path.Combine(nodeDirectory.FullName, "node");

            await File.WriteAllTextAsync(Path.Combine(installDirectory, "current.txt"), "test\n");
            await File.WriteAllTextAsync(Path.Combine(installDirectory, "port.txt"), "5077\n");
            await File.WriteAllTextAsync(node, "#!/usr/bin/env bash\nexit 0\n");
            await File.WriteAllTextAsync(Path.Combine(nvmDirectory.FullName, "nvm.sh"), $"nvm() {{ printf '%s\\n' '{node}'; }}\n");
            await File.WriteAllTextAsync(executable, $"#!/usr/bin/env bash\nprintf '%s\\n' \"$PATH\" > '{pathOutput}'\n");
            MakeExecutable(node);
            MakeExecutable(executable);

            var result = await RunLauncherAsync(Path.Combine(root, "scripts", "run.sh"), installDirectory, nvmDirectory.FullName);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(pathOutput), result.StandardError);
            var childPath = await File.ReadAllTextAsync(pathOutput);
            Assert.Contains(nodeDirectory.FullName, childPath, StringComparison.Ordinal);
        }
        finally
        {
            testDirectory.Delete(true);
        }
    }

    private static async Task<(int ExitCode, string StandardError)> RunLauncherAsync(
        string launcher,
        string installDirectory,
        string nvmDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/bash", [launcher, installDirectory])
            {
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.Environment["HOME"] = installDirectory;
        process.StartInfo.Environment["NVM_DIR"] = nvmDirectory;
        process.StartInfo.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        process.Start();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardError);
    }

    [UnsupportedOSPlatform("windows")]
    private static void MakeExecutable(string path) => File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private static string FindRepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
}
