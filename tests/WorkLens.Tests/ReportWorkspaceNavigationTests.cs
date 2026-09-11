namespace WorkLens.Tests;

public sealed class ReportWorkspaceNavigationTests
{
    [Fact]
    public async Task Reports_page_hosts_period_history_and_the_summary_workspace()
    {
        var reports = await ReadPageAsync("Components", "Pages", "Reports.razor");
        var workspace = await ReadPageAsync("Components", "ReportWorkspace.razor");
        var navigation = await ReadPageAsync("Components", "Layout", "NavMenu.razor");

        Assert.Contains("@page \"/reports\"", reports, StringComparison.Ordinal);
        Assert.Contains("ReportWorkspace", reports, StringComparison.Ordinal);
        Assert.Contains("RequestPeriodChangeAsync", reports, StringComparison.Ordinal);
        Assert.Contains("SaveChangesAsync", reports, StringComparison.Ordinal);
        Assert.Contains("篩選歷程", reports, StringComparison.Ordinal);
        Assert.Contains("僅篩選歷程，不影響摘要範圍", reports, StringComparison.Ordinal);
        Assert.Contains("<details class=\"panel history-browser\" open>", reports, StringComparison.Ordinal);
        Assert.Contains("點此展開／收合", reports, StringComparison.Ordinal);
        Assert.Contains("產生 AI 摘要", workspace, StringComparison.Ordinal);
        Assert.Contains("還原上一版", workspace, StringComparison.Ordinal);
        Assert.Contains("<section class=\"report-prompt-picker\"", workspace, StringComparison.Ordinal);
        Assert.Contains("<section class=\"report-secondary-actions\"", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("<details class=\"report-prompt-picker\"", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("<details class=\"report-secondary-actions\"", workspace, StringComparison.Ordinal);
        Assert.Contains("不受左側歷程篩選影響", workspace, StringComparison.Ordinal);
        Assert.Contains(
            "exception is JSException or JSDisconnectedException",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains("href=\"/reports\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("/history", navigation, StringComparison.Ordinal);
    }

    private static Task<string> ReadPageAsync(params string[] segments) =>
        File.ReadAllTextAsync(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. segments]));
}
