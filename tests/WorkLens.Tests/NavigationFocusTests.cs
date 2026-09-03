namespace WorkLens.Tests;

public sealed class NavigationFocusTests
{
    [Fact]
    public async Task Programmatic_heading_focus_is_kept_for_accessibility_without_a_visual_box()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var routes = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Routes.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains("<FocusOnNavigate", routes, StringComparison.Ordinal);
        Assert.Contains("Selector=\"h1\"", routes, StringComparison.Ordinal);
        Assert.Contains("h1[tabindex=\"-1\"]:focus { outline: none; }", css, StringComparison.Ordinal);
    }
}
