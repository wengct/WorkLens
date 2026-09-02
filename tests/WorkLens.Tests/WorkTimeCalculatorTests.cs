using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class WorkTimeCalculatorTests
{
    [Fact]
    public void UnionHours_merges_overlapping_manual_entries()
    {
        var day = new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.FromHours(8));
        var total = WorkTimeCalculator.UnionHours([
            new TimeInterval(day, day.AddHours(2)),
            new TimeInterval(day.AddHours(1), day.AddHours(3)),
            new TimeInterval(day.AddHours(5), day.AddHours(6))
        ]);

        Assert.Equal(4, total);
    }

    [Fact]
    public void UnionHours_supports_intervals_crossing_midnight()
    {
        var start = new DateTimeOffset(2026, 9, 2, 23, 30, 0, TimeSpan.FromHours(8));

        var total = WorkTimeCalculator.UnionHours([
            new TimeInterval(start, start.AddHours(2)),
            new TimeInterval(start.AddHours(1), start.AddHours(3))
        ]);

        Assert.Equal(3, total);
    }

    [Fact]
    public void UnionHours_ignores_invalid_intervals()
    {
        var start = new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.FromHours(8));

        var total = WorkTimeCalculator.UnionHours([
            new TimeInterval(start, start),
            new TimeInterval(start.AddHours(2), start.AddHours(1))
        ]);

        Assert.Equal(0, total);
    }
}
