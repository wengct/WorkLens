namespace WorkLens.Tests;

public sealed class ManualSourceUxTests
{
    [Fact]
    public async Task Today_page_renders_manual_sources_as_collapsible_items()
    {
        var razor = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "Components", "Pages", "Today.razor"));

        Assert.Contains("<details class=\"timeline-item manual-source-item\"", razor, StringComparison.Ordinal);
        Assert.Contains("@if (item.Kind == EvidenceKind.Manual)", razor, StringComparison.Ordinal);
        Assert.Contains("<summary>", razor, StringComparison.Ordinal);
        Assert.Contains("<div class=\"markdown-source\">@item.CommitMessage</div>", razor, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the WorkLens repository root.");
    }
}
