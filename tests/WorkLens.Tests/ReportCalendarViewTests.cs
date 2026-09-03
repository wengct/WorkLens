namespace WorkLens.Tests;

public sealed class ReportCalendarViewTests
{
    [Fact]
    public async Task Reports_calendar_supports_week_and_month_views()
    {
        var page = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "Pages", "Reports.razor"));

        Assert.Contains("aria-label=\"行事曆檢視\"", page, StringComparison.Ordinal);
        Assert.Contains("SetCalendarViewAsync(\"week\")", page, StringComparison.Ordinal);
        Assert.Contains("SetCalendarViewAsync(\"month\")", page, StringComparison.Ordinal);
        Assert.Contains("private string calendarView = \"week\"", page, StringComparison.Ordinal);
        Assert.Contains("string.Equals(Calendar, \"month\"", page, StringComparison.Ordinal);
        Assert.Contains("CalendarRange()", page, StringComparison.Ordinal);
        Assert.Contains("calendarAnchor.AddDays(offset * 7)", page, StringComparison.Ordinal);
        Assert.Contains("calendarAnchor.AddMonths(offset)", page, StringComparison.Ordinal);
    }
}
