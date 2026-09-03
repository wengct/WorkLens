namespace WorkLens;

public sealed class AppPaths
{
    public AppPaths(string databasePath, string backupPath, string logDirectory)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        BackupPath = Path.GetFullPath(backupPath);
        LogDirectory = Path.GetFullPath(logDirectory);
    }

    public string DatabasePath { get; }
    public string BackupPath { get; }
    public string LogDirectory { get; }
    public string DataDirectory => Path.GetDirectoryName(DatabasePath)!;

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupPath);
        Directory.CreateDirectory(LogDirectory);
    }

    public static string GetDefaultRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(userProfile, "Library", "Application Support", "WorkLens");
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkLens");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(
            string.IsNullOrWhiteSpace(xdgDataHome)
                ? Path.Combine(userProfile, ".local", "share")
                : xdgDataHome,
            "WorkLens");
    }

    public static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return expanded.Replace('/', Path.DirectorySeparatorChar);
    }
}
