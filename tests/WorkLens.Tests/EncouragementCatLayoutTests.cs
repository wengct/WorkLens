namespace WorkLens.Tests;

public sealed class EncouragementCatLayoutTests
{
    [Fact]
    public async Task Reduced_motion_keeps_the_cat_partially_hidden_until_opened()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains(
            ".encouragement-cat .encouragement-cat-art { transform: translate(calc(var(--cat-x) * 40px), calc(var(--cat-y) * 40px)) rotate(var(--cat-rotation)) !important; }",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            ".encouragement-cat[data-state=\"open\"] .encouragement-cat-art { transform: rotate(var(--cat-rotation)) !important; }",
            css,
            StringComparison.Ordinal);
    }
}
