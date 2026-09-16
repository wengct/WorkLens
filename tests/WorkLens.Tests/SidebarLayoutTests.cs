namespace WorkLens.Tests;

public sealed class SidebarLayoutTests
{
    [Fact]
    public async Task Sidebar_scrolls_when_navigation_and_footer_exceed_the_viewport()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var layout = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Layout", "MainLayout.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains("<aside class=\"sidebar @(mobileMenuOpen ? \"mobile-menu-open\" : null)\"", layout, StringComparison.Ordinal);
        Assert.Contains("<NavMenu OnNavigate=\"CloseMenu\" />", layout, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"main-navigation\"", layout, StringComparison.Ordinal);
        Assert.Contains("id=\"main-navigation\"", layout, StringComparison.Ordinal);
        Assert.Contains("<div class=\"sidebar-footer\">", layout, StringComparison.Ordinal);
        Assert.Contains(
            ".sidebar { width: 248px; position: fixed; inset: 0 auto 0 0; display: flex; flex-direction: column; overflow-y: auto; overscroll-behavior: contain;",
            css,
            StringComparison.Ordinal);
    }
}
