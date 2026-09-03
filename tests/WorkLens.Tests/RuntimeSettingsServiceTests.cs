using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class RuntimeSettingsServiceTests
{
    [Fact]
    public async Task Backup_path_is_validated_applied_and_loaded_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"worklens-settings-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "data");
        var initial = Path.Combine(root, "initial");
        var selected = Path.Combine(root, "selected");
        try
        {
            Directory.CreateDirectory(data);
            var paths = new AppPaths(Path.Combine(data, "worklens.db"), initial, Path.Combine(root, "logs"));
            var settings = new RuntimeSettingsService(paths);

            Assert.Equal(Path.GetFullPath(selected), await settings.SaveBackupPathAsync(selected));
            Assert.Equal(Path.GetFullPath(selected), settings.GetBackupPath());
            Assert.Equal(Path.GetFullPath(selected), new RuntimeSettingsService(paths).GetBackupPath());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
