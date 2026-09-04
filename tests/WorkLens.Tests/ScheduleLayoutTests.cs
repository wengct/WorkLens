namespace WorkLens.Tests;

public sealed class ScheduleLayoutTests
{
    [Fact]
    public async Task Schedule_cards_use_one_column_and_grid_gap_for_spacing()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Pages", "Schedules.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains(
            ".schedule-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: 18px; margin-top: 18px; }",
            css,
            StringComparison.Ordinal);
        Assert.Contains(".schedule-grid > .panel { margin-top: 0; }", css, StringComparison.Ordinal);
        Assert.Contains("<div class=\"schedule-settings-row\">", razor, StringComparison.Ordinal);
        Assert.Contains("<div class=\"schedule-card-meta\">", razor, StringComparison.Ordinal);
        Assert.Contains(".schedule-settings-row { display: flex;", css, StringComparison.Ordinal);
        Assert.Contains(".schedule-card { min-width: 0; padding: 18px 20px; }", css, StringComparison.Ordinal);
    }
}
