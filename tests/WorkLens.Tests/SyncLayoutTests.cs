namespace WorkLens.Tests;

public sealed class SyncLayoutTests
{
    [Fact]
    public async Task Sync_navigation_uses_a_distinct_icon_from_settings_transfer()
    {
        var root = RepositoryRoot();
        var navigation = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Layout", "NavMenu.razor"));

        Assert.Contains("<span class=\"nav-icon\" aria-hidden=\"true\">⟳</span>跨電腦同步", navigation, StringComparison.Ordinal);
        Assert.Contains("<span class=\"nav-icon\">⇄</span>設定移轉", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_page_uses_responsive_setup_status_and_scope_sections()
    {
        var root = RepositoryRoot();
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Pages", "Sync.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains("sync-page-stack", razor, StringComparison.Ordinal);
        Assert.Contains("sync-status-grid", razor, StringComparison.Ordinal);
        Assert.Contains("sync-scope-grid", razor, StringComparison.Ordinal);
        Assert.Contains("class=\"field sync-field sync-folder-field\"", razor, StringComparison.Ordinal);
        Assert.Contains("class=\"sync-folder-input\"", razor, StringComparison.Ordinal);
        Assert.Contains("status is null ? \"正在讀取\"", razor, StringComparison.Ordinal);
        Assert.Contains(".sync-page-stack > .panel { margin: 0; }", css, StringComparison.Ordinal);
        Assert.Contains(".sync-panel-heading > div:nth-child(2) { min-width: 0; }", css, StringComparison.Ordinal);
        Assert.Contains(".sync-panel-heading .badge { white-space: nowrap; }", css, StringComparison.Ordinal);
        Assert.Contains(".sync-folder-input { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 9px; }", css, StringComparison.Ordinal);
        Assert.Contains(".sync-form-grid, .sync-status-grid, .sync-scope-grid { grid-template-columns: 1fr; }", css, StringComparison.Ordinal);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
}
