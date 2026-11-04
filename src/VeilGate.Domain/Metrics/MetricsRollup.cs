using VeilGate.Domain.Messaging;

namespace VeilGate.Domain.Metrics;

/// <summary>What one gateway event adds to the counters of its hour. Every event is one request.</summary>
public sealed record MetricsDelta(
    DateTimeOffset Hour, Guid ClientId, int Blocked, int Failed, long Entities,
    IReadOnlyDictionary<string, int> EntitiesByType, string? LatencyBand);

/// <summary>The rules for turning events into hourly counters. The database only stores what these rules produce.</summary>
public static class MetricsRollup
{
    /// <summary>The start of the UTC hour that the time falls in.</summary>
    public static DateTimeOffset HourOf(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>"Blocked" means the call was denied (a blocking policy, a refused re-identify); "failed" means something went wrong (for example the provider).</summary>
    public static MetricsDelta DeltaFor(GatewayRequestEvent e) => new(
        HourOf(e.OccurredAt), e.ClientId,
        Blocked: e.Result == "denied" ? 1 : 0,
        Failed: e.Result == "failure" ? 1 : 0,
        Entities: e.Entities.Values.Sum(v => (long)v),
        EntitiesByType: e.Entities,
        LatencyBand: e.LatencyMs is { } ms ? LatencyBands.KeyFor(ms) : null);

    /// <summary>The counters after adding the delta. A new hour starts from zero.</summary>
    public static MetricsHour Merge(MetricsHour? current, Guid tenantId, MetricsDelta delta)
    {
        var buckets = Add(current?.LatencyBuckets, delta.LatencyBand is null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int> { [delta.LatencyBand] = 1 });

        return new MetricsHour
        {
            TenantId = tenantId, Hour = delta.Hour, ClientId = delta.ClientId,
            Requests = (current?.Requests ?? 0) + 1,
            Blocked = (current?.Blocked ?? 0) + delta.Blocked,
            Failed = (current?.Failed ?? 0) + delta.Failed,
            Entities = (current?.Entities ?? 0) + delta.Entities,
            EntitiesByType = Add(current?.EntitiesByType, delta.EntitiesByType),
            LatencyBuckets = buckets,
        };
    }

    /// <summary>Adds two name-to-count maps key by key. Neither input is changed.</summary>
    public static Dictionary<string, int> Add(IReadOnlyDictionary<string, int>? a, IReadOnlyDictionary<string, int> b)
    {
        var sum = a is null ? new Dictionary<string, int>() : new Dictionary<string, int>(a);
        foreach (var (key, count) in b) sum[key] = sum.GetValueOrDefault(key) + count;
        return sum;
    }
}
