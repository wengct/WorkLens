namespace WorkLens.Tests;

public sealed class ReportCopyTests
{
    [Fact]
    public async Task Reports_page_can_copy_the_current_report_body()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var reportWorkspace = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "Components", "Pages", "Reports.razor"));
        var appShell = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "Components", "App.razor"));

        Assert.Contains("ReportWorkspace", reportWorkspace, StringComparison.Ordinal);
        var workspace = await File.ReadAllTextAsync(Path.Combine(
            repositoryRoot, "Components", "ReportWorkspace.razor"));
        Assert.Contains("@onclick=\"CopyReportAsync\"", workspace, StringComparison.Ordinal);
        Assert.Contains("workLensClipboard.copyText", workspace, StringComparison.Ordinal);
        Assert.Contains("js/clipboard-interop.js", appShell, StringComparison.Ordinal);
    }
}
