namespace WorkLens.Tests;

public sealed class SourceSetupUxTests
{
    [Fact]
    public async Task Setup_uses_content_and_location_instead_of_platform_cartesian_options()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("要收集什麼？", razor, StringComparison.Ordinal);
        Assert.Contains("資料位於哪裡？", razor, StringComparison.Ordinal);
        Assert.Contains("Git repository", razor, StringComparison.Ordinal);
        Assert.Contains("Codex 工作紀錄", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"WindowsGit\">", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"MacOsGit\">", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_makes_optional_configuration_progressive()
    {
        var razor = await ReadSourcesPageAsync();

        Assert.Contains("來源名稱（選填）", razor, StringComparison.Ordinal);
        Assert.Contains("<details class=\"source-advanced full\">", razor, StringComparison.Ordinal);
        Assert.Contains("進階設定", razor, StringComparison.Ordinal);
    }

    private static async Task<string> ReadSourcesPageAsync()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "Pages", "Sources.razor"));
        return await File.ReadAllTextAsync(path);
    }
}
