namespace WorkLens.Tests;

public sealed class LongRunningOperationProgressTests
{
    [Theory]
    [InlineData("Components/ReportWorkspace.razor", "isGeneratingReport", "正在產生摘要")]
    [InlineData("Components/ReportWorkspace.razor", "isGeneratingAi", "AI 正在整理摘要")]
    [InlineData("Components/Pages/Reports.razor", "isBackfilling", "正在重新收集來源")]
    [InlineData("Components/Pages/AiSettings.razor", "activeOperation", "正在測試 AI 連線")]
    [InlineData("Components/Pages/Sources.razor", "validatingSourceIds", "正在驗證來源")]
    public async Task Long_running_pages_render_operation_progress(
        string relativePath,
        string stateMarker,
        string userFacingText)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            relativePath));
        var razor = await File.ReadAllTextAsync(path);

        Assert.Contains("<OperationProgress", razor, StringComparison.Ordinal);
        Assert.Contains(stateMarker, razor, StringComparison.Ordinal);
        Assert.Contains(userFacingText, razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_progress_component_is_accessible_and_uses_a_progress_track()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "OperationProgress.razor"));
        var razor = await File.ReadAllTextAsync(path);

        Assert.Contains("role=\"status\"", razor, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", razor, StringComparison.Ordinal);
        Assert.Contains("operation-progress-track", razor, StringComparison.Ordinal);
    }
}
