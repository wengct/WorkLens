using System.Text.Json;

namespace WorkLens.Services;

public sealed class RuntimeSettingsService
{
    private readonly AppPaths paths;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string settingsPath;
    private string? backupPathOverride;

    public RuntimeSettingsService(AppPaths paths)
    {
        this.paths = paths;
        settingsPath = Path.Combine(paths.DataDirectory, "runtime-settings.json");
        backupPathOverride = Load()?.BackupPath;
    }

    public string GetBackupPath() => string.IsNullOrWhiteSpace(backupPathOverride)
        ? paths.BackupPath
        : Path.GetFullPath(AppPaths.ExpandPath(backupPathOverride));

    public async Task<string> SaveBackupPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("備份路徑不可空白。", nameof(path));
        }

        var resolved = Path.GetFullPath(AppPaths.ExpandPath(path));
        Directory.CreateDirectory(resolved);
        var probe = Path.Combine(resolved, $".worklens-write-test-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probe, "WorkLens", cancellationToken);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var temporaryPath = settingsPath + ".new";
            var content = JsonSerializer.Serialize(
                new RuntimeSettings(resolved),
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, settingsPath, true);
            backupPathOverride = resolved;
        }
        finally
        {
            gate.Release();
        }

        return resolved;
    }

    private RuntimeSettings? Load()
    {
        try
        {
            return File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(settingsPath))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RuntimeSettings(string BackupPath);
}
