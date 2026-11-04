using VeilGate.Domain.Messaging;

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

    /// <summary>The Requests-page row for an event. <paramref name="messageId"/> is the id of the message that carried it.</summary>
    public static GatewayRequestRecord From(Guid messageId, GatewayRequestEvent e) => new()
    {
        Id = messageId, TenantId = e.TenantId, ClientId = e.ClientId, ActorType = e.ActorType, RequestId = e.RequestId,
        Action = e.Action, Status = e.Result, Reason = e.Reason, Model = e.Model, Provider = e.Provider, PolicyVersion = e.PolicyVersion,
        EntityCount = e.Entities.Values.Sum(), Entities = new Dictionary<string, int>(e.Entities), LatencyMs = e.LatencyMs, CreatedAt = e.OccurredAt,
    };
}
