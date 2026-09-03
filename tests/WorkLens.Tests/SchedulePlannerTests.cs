using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class SchedulePlannerTests
{
    [Fact]
    public void Due_requires_enabled_selected_day_and_time()
    {
        var schedule = new ScheduleDefinition
        {
            Enabled = true,
            DaysOfWeekMask = 1 << (int)DayOfWeek.Friday,
            Hour = 17,
            Minute = 30
        };

        Assert.False(SchedulePlanner.IsDue(schedule, new DateTime(2026, 9, 4, 17, 29, 0)));
        Assert.True(SchedulePlanner.IsDue(schedule, new DateTime(2026, 9, 4, 17, 30, 0)));
        Assert.False(SchedulePlanner.IsDue(schedule, new DateTime(2026, 9, 5, 18, 0, 0)));
        schedule.Enabled = false;
        Assert.False(SchedulePlanner.IsDue(schedule, new DateTime(2026, 9, 4, 18, 0, 0)));
    }

    [Fact]
    public void Next_run_moves_to_the_next_selected_day()
    {
        var schedule = new ScheduleDefinition
        {
            Enabled = true,
            DaysOfWeekMask = ScheduleDefaults.WeekdaysMask,
            Hour = 17,
            Minute = 30
        };

        Assert.Equal(
            new DateTime(2026, 9, 7, 17, 30, 0),
            SchedulePlanner.NextRun(schedule, new DateTime(2026, 9, 4, 18, 0, 0)));
    }

    [Theory]
    [InlineData(ScheduleKind.DailyReport, "2026-09-04")]
    [InlineData(ScheduleKind.DailyBackup, "2026-09-04")]
    [InlineData(ScheduleKind.WeeklyReport, "2026-W36")]
    [InlineData(ScheduleKind.WeeklyBackup, "2026-W36")]
    public void Period_key_matches_schedule_cycle(ScheduleKind kind, string expected) =>
        Assert.Equal(expected, SchedulePlanner.PeriodKey(kind, new DateOnly(2026, 9, 4)));
}
