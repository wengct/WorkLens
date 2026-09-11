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
        Assert.True(
            reports.IndexOf("<details class=\"panel history-browser\" open>", StringComparison.Ordinal) >
            reports.IndexOf("<ReportWorkspace", StringComparison.Ordinal));
        Assert.Contains("class=\"summary-workspace\"", reports, StringComparison.Ordinal);
        Assert.Contains("class=\"report-period-toolbar\"", reports, StringComparison.Ordinal);
        Assert.Contains("產生 AI 摘要", workspace, StringComparison.Ordinal);
        Assert.Contains("還原上一版", workspace, StringComparison.Ordinal);
        Assert.Contains("<div class=\"report-main-toolbar\"", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("report-secondary-actions", workspace, StringComparison.Ordinal);
        Assert.Contains("不受下方歷程篩選影響", workspace, StringComparison.Ordinal);
        Assert.Contains(
            "exception is JSException or JSDisconnectedException",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains("href=\"/reports\"", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("/history", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_actions_remain_available_in_the_main_toolbar_with_consistent_panel_spacing()
    {
        var reports = await ReadPageAsync("Components", "Pages", "Reports.razor");
        var workspace = await ReadPageAsync("Components", "ReportWorkspace.razor");
        var css = await ReadPageAsync("wwwroot", "app.css");
        var toolbarStart = workspace.IndexOf("<div class=\"report-main-toolbar\"", StringComparison.Ordinal);
        var reportBodyStart = workspace.IndexOf("id=\"report-body-editor\"", StringComparison.Ordinal);

        Assert.InRange(workspace.IndexOf("產生基本摘要", StringComparison.Ordinal), toolbarStart, reportBodyStart - 1);
        Assert.InRange(workspace.IndexOf("下載 Markdown", StringComparison.Ordinal), toolbarStart, reportBodyStart - 1);
        Assert.InRange(workspace.IndexOf("下載 CSV", StringComparison.Ordinal), toolbarStart, reportBodyStart - 1);
        Assert.InRange(workspace.IndexOf("還原上一版", StringComparison.Ordinal), toolbarStart, reportBodyStart - 1);
        Assert.Contains("class=\"report-page-stack\"", reports, StringComparison.Ordinal);
        Assert.True(reports.IndexOf("<ReportWorkspace", StringComparison.Ordinal) < reports.IndexOf("history-filter-panel", StringComparison.Ordinal));
        Assert.True(reports.IndexOf("history-filter-panel", StringComparison.Ordinal) < reports.IndexOf("history-day-detail", StringComparison.Ordinal));
        Assert.Contains(".summary-workspace { display: grid; grid-template-columns: minmax(0, 1fr) minmax(340px, .65fr);", css, StringComparison.Ordinal);
        Assert.Contains(".history-browser .history-calendar-grid-week { grid-template-columns: minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains(".report-history-stack > .panel { margin-top: 0; }", css, StringComparison.Ordinal);
        Assert.Contains("class=\"button-row report-utility-actions\"", workspace, StringComparison.Ordinal);
        Assert.Contains(".report-page-stack { display: grid; gap: 24px; }", css, StringComparison.Ordinal);
    }

    private static Task<string> ReadPageAsync(params string[] segments) =>
        File.ReadAllTextAsync(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. segments]));
}
