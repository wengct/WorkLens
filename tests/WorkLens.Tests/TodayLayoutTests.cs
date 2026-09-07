namespace WorkLens.Tests;

public sealed class TodayLayoutTests
{
    [Fact]
    public async Task Timeline_content_wraps_long_unbroken_values_inside_panels()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains(".timeline-item { position: relative; min-width: 0;", css, StringComparison.Ordinal);
        Assert.Contains(".timeline-title {", css, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", css, StringComparison.Ordinal);
        Assert.Contains(".timeline-detail {", css, StringComparison.Ordinal);
    }
}
