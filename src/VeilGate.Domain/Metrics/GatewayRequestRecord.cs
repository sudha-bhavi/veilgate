namespace VeilGate.Domain.Metrics;

/// <summary>
/// One gateway call as the Requests page lists it. Built from a gateway event by the metrics consumer, never by the gateway itself.
/// The id is the id of the message that carried the event (request ids are short and can repeat over time).
/// Like the audit log it holds counts and names only, never text.
/// </summary>
public sealed class GatewayRequestRecord
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>The API client that made the call, or the user when ActorType is "user".</summary>
    public Guid ClientId { get; init; }

    public string ActorType { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;

    /// <summary>success, failure or denied.</summary>
    public string Status { get; init; } = string.Empty;

    public string? Reason { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public string? PolicyVersion { get; init; }
    public int EntityCount { get; init; }
    public IReadOnlyDictionary<string, int> Entities { get; init; } = new Dictionary<string, int>();
    public long? LatencyMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
