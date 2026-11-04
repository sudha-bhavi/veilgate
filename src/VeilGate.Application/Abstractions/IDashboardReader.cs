using VeilGate.Domain.Metrics;

namespace VeilGate.Application.Abstractions;

/// <summary>The counters of the current tenant over a span, summed over all hours and clients.</summary>
public sealed record DashboardTotals(
    long Requests, long Blocked, long Failed, long Entities,
    IReadOnlyDictionary<string, int> EntitiesByType, IReadOnlyDictionary<string, int> LatencyBuckets);

/// <summary>A caller and how many requests it made. Name is null when the client or person no longer exists.</summary>
public sealed record ClientTotal(Guid ClientId, string? Name, long Requests);

/// <summary>Read-only access to the hourly counters of the current tenant. Hours are UTC; "to" is exclusive.</summary>
public interface IDashboardReader
{
    Task<DashboardTotals> TotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<IReadOnlyList<ClientTotal>> TopClientsAsync(DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default);

    Task<IReadOnlyList<HourlyPoint>> HourlyAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>The leak rate of the newest successful evaluation run, or null when there has been none.</summary>
    Task<double?> LatestLeakRateAsync(CancellationToken ct = default);
}
