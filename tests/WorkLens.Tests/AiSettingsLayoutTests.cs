namespace WorkLens.Tests;

public sealed class AiSettingsLayoutTests
{
    [Fact]
    public async Task Environment_diagnostics_keeps_spacing_after_the_settings_grid()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Pages", "AiSettings.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains("<div class=\"ai-settings-stack\">", razor, StringComparison.Ordinal);
        Assert.Contains(".ai-settings-stack { display: grid; gap: 18px; }", css, StringComparison.Ordinal);
        Assert.Contains(".ai-settings-stack > .panel { margin-top: 0; }", css, StringComparison.Ordinal);
    }
}
