using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Metrics;

namespace VeilGate.Application.Dashboard;

/// <summary>The one-screen picture. Latencies are the upper limit of a latency band, not an exact time.</summary>
public sealed record DashboardSummary(
    string Range, DateTimeOffset From, DateTimeOffset To, long Requests, long EntitiesProtected, long BlockedRequests, long FailedRequests,
    int? P50LatencyMs, int? P95LatencyMs, double? LatestLeakRate,
    IReadOnlyDictionary<string, int> EntitiesByType, IReadOnlyList<ClientTotal> TopClients);

public sealed record DashboardSeries(string Range, string Metric, IReadOnlyList<SeriesPoint> Points);

public sealed class GetDashboardSummaryHandler(IDashboardReader reader, IClock clock)
{
    public const string DefaultRange = "24h";
    public const int TopClientCount = 5;

    public async Task<Result<DashboardSummary>> HandleAsync(string? range, CancellationToken ct = default)
    {
        var parsed = DashboardRange.Parse(range ?? DefaultRange);
        if (parsed is null) return Error.Validation("validation.failed", "Range must be 24h, 7d or 30d.");

        var now = clock.UtcNow;
        var (from, to) = (parsed.Start(now), parsed.End(now));

        var totals = await reader.TotalsAsync(from, to, ct);
        var top = await reader.TopClientsAsync(from, to, TopClientCount, ct);
        var leakRate = await reader.LatestLeakRateAsync(ct);

        return new DashboardSummary(
            parsed.Key, from, to, totals.Requests, totals.Entities, totals.Blocked, totals.Failed,
            LatencyBands.Percentile(totals.LatencyBuckets, 0.50), LatencyBands.Percentile(totals.LatencyBuckets, 0.95),
            leakRate, totals.EntitiesByType, top);
    }
}

public sealed class GetDashboardSeriesHandler(IDashboardReader reader, IClock clock)
{
    public async Task<Result<DashboardSeries>> HandleAsync(string? range, string? metric, CancellationToken ct = default)
    {
        var parsed = DashboardRange.Parse(range ?? GetDashboardSummaryHandler.DefaultRange);
        if (parsed is null) return Error.Validation("validation.failed", "Range must be 24h, 7d or 30d.");
        if (metric is null || !MetricsSeries.Metrics.Contains(metric))
            return Error.Validation("validation.failed", "Metric must be requests, entities or latency.");

        var now = clock.UtcNow;
        var hours = await reader.HourlyAsync(parsed.Start(now), parsed.End(now), ct);
        return new DashboardSeries(parsed.Key, metric, MetricsSeries.Build(hours, parsed, now, metric));
    }
}
