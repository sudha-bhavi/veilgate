namespace VeilGate.Domain.Metrics;

/// <summary>
/// The counters for one tenant, one client and one hour. The consumer adds to them; the Dashboard reads them.
/// Latency is kept as a histogram (how many calls fell in each band) because a percentile cannot be updated by adding one more
/// call, while a histogram can, and p50 and p95 are worked out from it when the Dashboard asks.
/// </summary>
public sealed class MetricsHour
{
    public Guid TenantId { get; init; }

    /// <summary>The start of the hour, in UTC.</summary>
    public DateTimeOffset Hour { get; init; }

    public Guid ClientId { get; init; }
    public int Requests { get; init; }
    public int Blocked { get; init; }
    public int Failed { get; init; }

    /// <summary>How many sensitive values were protected in total.</summary>
    public long Entities { get; init; }

    public IReadOnlyDictionary<string, int> EntitiesByType { get; init; } = new Dictionary<string, int>();

    /// <summary>Band upper limit in milliseconds, then how many calls fell in it. See LatencyBands.</summary>
    public IReadOnlyDictionary<string, int> LatencyBuckets { get; init; } = new Dictionary<string, int>();
}
