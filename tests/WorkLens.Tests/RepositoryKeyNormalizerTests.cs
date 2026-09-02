using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class RepositoryKeyNormalizerTests
{
    [Fact]
    public void Windows_and_wsl_mount_paths_share_one_physical_repository_key()
    {
        var windows = RepositoryKeyNormalizer.Normalize(
            ActivitySourceType.WindowsGit,
            null,
            @"C:\Data\Projects\WorkLens");
        var wsl = RepositoryKeyNormalizer.Normalize(
            ActivitySourceType.WslGit,
            "Ubuntu",
            "/mnt/c/Data/Projects/WorkLens");

        Assert.Equal("physical:c:/data/projects/worklens", windows);
        Assert.Equal(windows, wsl);
    }

    [Fact]
    public void Non_mount_wsl_paths_remain_distro_specific()
    {
        var key = RepositoryKeyNormalizer.Normalize(
            ActivitySourceType.WslGit,
            "Ubuntu",
            "/home/user/project");

        Assert.Equal("WslGit:ubuntu:/home/user/project", key);
    }
}
