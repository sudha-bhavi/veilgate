namespace VeilGate.Domain.Metrics;

/// <summary>
/// Latency is counted in bands instead of stored as percentiles, because one more call can be added to a band by adding 1,
/// but a percentile cannot be updated that way. p50 and p95 are worked out from the bands when they are read.
/// A band is named by its upper limit in milliseconds: "100" holds calls that took more than 50 and up to 100 ms.
/// </summary>
public static class LatencyBands
{
    public static readonly IReadOnlyList<int> Limits = [25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    /// <summary>The band for calls slower than the last limit.</summary>
    public const string Overflow = "inf";

    public static string KeyFor(long ms)
    {
        foreach (var limit in Limits)
            if (ms <= limit) return limit.ToString();
        return Overflow;
    }

    /// <summary>
    /// The upper limit of the band that holds the given percentile (0 to 1), for example 0.95 for p95. The answer is a band's
    /// limit, not an exact time. For the overflow band it is the last limit, meaning "at least this". Null when there are no calls.
    /// </summary>
    public static int? Percentile(IReadOnlyDictionary<string, int> buckets, double percentile)
    {
        if (percentile is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be above 0 and at most 1.");

        var total = buckets.Values.Sum();
        if (total == 0) return null;

        var rank = (int)Math.Ceiling(percentile * total);
        var counted = 0;
        foreach (var limit in Limits)
        {
            counted += buckets.GetValueOrDefault(limit.ToString());
            if (counted >= rank) return limit;
        }
        return Limits[^1];   // the rank falls in the overflow band
    }
}
