namespace WorkLens.Services;

public readonly record struct TimeInterval(DateTimeOffset Start, DateTimeOffset End);

public static class WorkTimeCalculator
{
    public static double UnionHours(IEnumerable<TimeInterval> intervals)
    {
        var ordered = intervals
            .Where(x => x.End > x.Start)
            .OrderBy(x => x.Start)
            .ToArray();

        if (ordered.Length == 0)
        {
            return 0;
        }

        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].End;
        var total = TimeSpan.Zero;

        foreach (var interval in ordered.Skip(1))
        {
            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd)
                {
                    currentEnd = interval.End;
                }

                continue;
            }

            total += currentEnd - currentStart;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }

        total += currentEnd - currentStart;
        return total.TotalHours;
    }
}
