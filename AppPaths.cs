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

    public static string ExpandPath(string path) =>
        Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));
}
