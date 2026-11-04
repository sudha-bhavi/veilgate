namespace VeilGate.Domain.Metrics;

/// <summary>The counters of one hour, summed over all clients.</summary>
public sealed record HourlyPoint(DateTimeOffset Hour, long Requests, long Entities, IReadOnlyDictionary<string, int> LatencyBuckets);

/// <summary>One point of a chart. The value is null when there is nothing to show, for example the p95 of an hour with no calls.</summary>
public sealed record SeriesPoint(DateTimeOffset T, long? Value);

public static class MetricsSeries
{
    public const string Requests = "requests";
    public const string Entities = "entities";
    public const string Latency = "latency";

    public static readonly IReadOnlySet<string> Metrics = new HashSet<string> { Requests, Entities, Latency };

    /// <summary>
    /// One point for every hour or day of the range, including those with no activity (counts are 0, latency is null), so a
    /// chart shows quiet periods instead of skipping them. Latency is the p95 band limit of the calls in that point.
    /// </summary>
    public static IReadOnlyList<SeriesPoint> Build(IEnumerable<HourlyPoint> hours, DashboardRange range, DateTimeOffset now, string metric)
    {
        var all = hours.ToList();
        var start = range.Start(now);
        var points = new List<SeriesPoint>(range.Buckets);

        for (var i = 0; i < range.Buckets; i++)
        {
            var from = start + i * range.Bucket;
            var to = from + range.Bucket;
            var inside = all.Where(h => h.Hour >= from && h.Hour < to).ToList();

            long? value = metric switch
            {
                Requests => inside.Sum(h => h.Requests),
                Entities => inside.Sum(h => h.Entities),
                Latency => LatencyBands.Percentile(
                    inside.Aggregate(new Dictionary<string, int>(), (sum, h) => MetricsRollup.Add(sum, h.LatencyBuckets)), 0.95),
                _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown metric."),
            };
            points.Add(new SeriesPoint(from, value));
        }
        return points;
    }
}
