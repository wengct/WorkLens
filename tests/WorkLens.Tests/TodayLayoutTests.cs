namespace WorkLens.Tests;

public sealed class TodayLayoutTests
{
    [Fact]
    public async Task Unfinished_drafts_are_nested_above_work_records()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Pages", "Today.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));
        Assert.True(razor.IndexOf("id=\"today-records\"", StringComparison.Ordinal) < razor.IndexOf("class=\"unfinished-drafts\"", StringComparison.Ordinal));
        Assert.DoesNotContain("class=\"panel unfinished-drafts\"", razor, StringComparison.Ordinal);
        Assert.Contains(".today-records .unfinished-drafts {", css, StringComparison.Ordinal);
        Assert.Contains(".today-page-stack { display: flex; flex-direction: column; gap: 14px;", css);
        Assert.Contains(".today-page-stack > .panel { margin-top: 0; }", css);
        Assert.Contains(".today-page-stack > .page-heading, .today-page-stack > .today-stats { margin: 0; }", css);
    }

    [Fact]
    public async Task Work_input_keeps_secondary_actions_in_header_and_uses_compact_spacing()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Pages", "Today.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(root, "wwwroot", "app.css"));

        Assert.Contains("class=\"button-row today-quick-actions\"", razor, StringComparison.Ordinal);
        Assert.Contains(".today-quick-add { padding: 18px; }", css, StringComparison.Ordinal);
        Assert.Contains("resize: vertical;", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_hours_support_half_hour_keyboard_steps()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "WorkDraftEditor.razor"));

        Assert.Contains("type=\"number\" data-draft-field=\"hours\"", razor, StringComparison.Ordinal);
        Assert.Contains("min=\"0.5\" max=\"24\" step=\"0.5\"", razor, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task First_summary_guide_is_available_from_the_work_input_header_and_uses_a_modal()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var razor = await File.ReadAllTextAsync(Path.Combine(root, "Components", "Pages", "Today.razor"));

        Assert.Contains("建立第一份工作摘要", razor, StringComparison.Ordinal);
        Assert.Contains("class=\"modal-dialog getting-started-dialog\"", razor, StringComparison.Ordinal);
        Assert.Contains("RuntimeSettings.IsOnboardingDismissed", razor, StringComparison.Ordinal);
        Assert.Contains("SetOnboardingDismissedAsync(true)", razor, StringComparison.Ordinal);
    }
}
