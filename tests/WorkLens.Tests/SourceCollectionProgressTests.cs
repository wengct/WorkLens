namespace WorkLens.Tests;

public sealed class SourceCollectionProgressTests
{
    [Fact]
    public async Task Sources_page_exposes_live_collection_progress_and_prevents_duplicate_runs()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "Pages", "Sources.razor"));
        var razor = await File.ReadAllTextAsync(path);

        Assert.Contains("collection-progress", razor, StringComparison.Ordinal);
        Assert.Contains("collectingSourceIds.Contains(source.Id)", razor, StringComparison.Ordinal);
        Assert.Contains("PeriodicTimer", razor, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", razor, StringComparison.Ordinal);
    }
}
