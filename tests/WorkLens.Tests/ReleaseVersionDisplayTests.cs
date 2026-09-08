namespace WorkLens.Tests;

public sealed class ReleaseVersionDisplayTests
{
    [Fact]
    public void Sidebar_uses_the_runtime_release_version()
    {
        var layout = File.ReadAllText(FindRepositoryFile("Components", "Layout", "MainLayout.razor"));
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        Assert.Contains("v@(StartupHealth.Version)", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("v@StartupHealth.Version</div>", layout, StringComparison.Ordinal);
        Assert.Contains("v@(updateStatus.CurrentVersion)", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("v@updateStatus.CurrentVersion。", layout, StringComparison.Ordinal);
        Assert.Contains("-p:Version=$version", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:InformationalVersion=$version", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
