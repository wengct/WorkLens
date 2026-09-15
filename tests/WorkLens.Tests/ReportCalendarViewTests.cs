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

    [Fact]
    public async Task Reports_week_calendar_shows_month_day_and_weekday()
    {
        var page = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Components", "Pages", "Reports.razor"));

        Assert.Contains("@CalendarDayLabel(day)", page, StringComparison.Ordinal);
        Assert.Contains("$\"{day.Month}/{day.Day}({WeekWeekdays", page, StringComparison.Ordinal);
        Assert.Contains("calendarView == \"week\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Week_calendar_reserves_space_for_the_today_label()
    {
        var css = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "wwwroot", "app.css"));

        Assert.Contains(
            ".history-browser .history-calendar-grid-week .calendar-day { display: grid; grid-template-columns: 92px 54px minmax(0, 1fr) auto;",
            css,
            StringComparison.Ordinal);
    }
}
