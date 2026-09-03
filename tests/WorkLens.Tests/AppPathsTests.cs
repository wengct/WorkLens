using WorkLens;

namespace WorkLens.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void ExpandPath_expands_a_home_relative_path()
    {
        var path = AppPaths.ExpandPath("~/WorkLens/data");

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "WorkLens",
                "data"),
            path);
    }

    [Fact]
    public void Default_root_is_an_absolute_user_data_path()
    {
        var path = AppPaths.GetDefaultRoot();

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith("WorkLens", path, StringComparison.Ordinal);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(
                Path.Combine("Library", "Application Support", "WorkLens"),
                path,
                StringComparison.Ordinal);
        }
    }
}
