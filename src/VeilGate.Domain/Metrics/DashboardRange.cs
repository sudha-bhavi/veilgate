namespace VeilGate.Domain.Metrics;

/// <summary>
/// A time span the Dashboard can show: the last 24 hours in hourly points, or the last 7 or 30 days in daily points.
/// The current (unfinished) hour or day is the last point. Days are UTC days, the same clock the hourly counters use.
/// </summary>
public sealed record DashboardRange(string Key, TimeSpan Bucket, int Buckets)
{
    public static readonly IReadOnlyList<DashboardRange> All =
        [new("24h", TimeSpan.FromHours(1), 24), new("7d", TimeSpan.FromDays(1), 7), new("30d", TimeSpan.FromDays(1), 30)];

    public static DashboardRange? Parse(string? key) => All.FirstOrDefault(r => r.Key == key);

    /// <summary>The start of the first point. Hours before it are not shown.</summary>
    public DateTimeOffset Start(DateTimeOffset now)
    {
        var hour = MetricsRollup.HourOf(now);
        var anchor = Bucket < TimeSpan.FromDays(1) ? hour : new DateTimeOffset(hour.Year, hour.Month, hour.Day, 0, 0, 0, TimeSpan.Zero);
        return anchor - (Buckets - 1) * Bucket;
    }

    /// <summary>The end of the current hour. Hours from here on are not included.</summary>
    public DateTimeOffset End(DateTimeOffset now) => MetricsRollup.HourOf(now).AddHours(1);
}
