namespace WorkLens.Tests;

public sealed class SourceEvidenceUxTests
{
    [Fact]
    public async Task Today_page_allows_deleting_an_automatic_source_activity_with_a_local_only_warning()
    {
        var razor = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "Components", "Pages", "Today.razor"));

        Assert.Contains("@onclick=\"() => pendingSourceEvidenceDeleteId = item.Id\">刪除本機紀錄</button>", razor, StringComparison.Ordinal);
        Assert.Contains("只會刪除 WorkLens 本機紀錄，不會修改原始來源。", razor, StringComparison.Ordinal);
        Assert.Contains("若原始資料仍符合條件，重新收集時可能再次匯入。", razor, StringComparison.Ordinal);
        Assert.Contains("await SourceEvidence.DeleteAsync(id);", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Today_page_allows_deleting_the_selected_dates_automatic_activities()
    {
        var razor = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "Components", "Pages", "Today.razor"));

        Assert.Contains(">刪除本日全部自動活動</button>", razor, StringComparison.Ordinal);
        Assert.Contains("Title=\"刪除本日全部自動活動？\"", razor, StringComparison.Ordinal);
        Assert.Contains("保留手動參考資料、其他日期與來源設定", razor, StringComparison.Ordinal);
        Assert.Contains("原始來源不受影響", razor, StringComparison.Ordinal);
        Assert.Contains("await SourceEvidence.DeleteAutomaticForDateAsync(selectedDate);", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_daily_view_allows_deleting_the_selected_dates_automatic_activities()
    {
        var razor = await File.ReadAllTextAsync(Path.Combine(
            FindRepositoryRoot(),
            "Components", "Pages", "Reports.razor"));

        Assert.Contains("@if (kind == ReportKind.Daily)", razor, StringComparison.Ordinal);
        Assert.Contains(">刪除本日全部自動活動</button>", razor, StringComparison.Ordinal);
        Assert.Contains("Title=\"刪除本日全部自動活動？\"", razor, StringComparison.Ordinal);
        Assert.Contains("不受目前篩選條件影響", razor, StringComparison.Ordinal);
        Assert.Contains("手動參考資料、其他日期、來源設定與原始來源不受影響", razor, StringComparison.Ordinal);
        Assert.Contains("await SourceEvidence.DeleteAutomaticForDateAsync(selectedDate);", razor, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the WorkLens repository root.");
    }
}
