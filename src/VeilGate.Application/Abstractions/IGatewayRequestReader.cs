namespace VeilGate.Application.Abstractions;

/// <summary>What the request list can be narrowed to. Every part is optional; all given parts must match.</summary>
public sealed record GatewayRequestFilter(
    DateTimeOffset? From, DateTimeOffset? To, string? Action, string? Status, Guid? ClientId, string? RequestId);

/// <summary>
/// One gateway call as the console shows it: counts and names only, never the text that went through.
/// ClientName is the API client's (or person's) name at the time of reading, or null when it no longer exists.
/// </summary>
public sealed record GatewayRequestView(
    Guid Id, string RequestId, DateTimeOffset OccurredAt, string ActorType, Guid ClientId, string? ClientName, string Action,
    string Status, string? Reason, string? Model, string? Provider, string? PolicyVersion, int EntityCount,
    IReadOnlyDictionary<string, int> Entities, long? LatencyMs);

/// <summary>Read-only access to the current tenant's gateway requests, newest first.</summary>
public interface IGatewayRequestReader
{
    Task<(IReadOnlyList<GatewayRequestView> Items, int Total)> ListAsync(GatewayRequestFilter filter, int page, int pageSize, CancellationToken ct = default);

    Task<GatewayRequestView?> FindAsync(Guid id, CancellationToken ct = default);

    /// <summary>The action names that occur in the tenant's requests, for a filter list.</summary>
    Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken ct = default);
}
