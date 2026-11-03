using VeilGate.Domain.Common;

namespace VeilGate.Domain.Messaging;

/// <summary>
/// What is published on vg.gateway.requests.v1 for each gateway call. It feeds the dashboard counters and the Requests page,
/// so it carries only counts and names: never text, never a value that was found, never a token.
/// </summary>
public sealed record GatewayRequestEvent
{
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>
        { "gateway.detect", "gateway.deidentify", "gateway.reidentify", "gateway.chat" };

    public static readonly IReadOnlySet<string> Results = new HashSet<string> { "success", "failure", "denied" };

    public const int MaxTextLength = 200;

    private GatewayRequestEvent() { }

    public string RequestId { get; private init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private init; }
    public Guid TenantId { get; private init; }
    public Guid ClientId { get; private init; }
    public string ActorType { get; private init; } = string.Empty;
    public string Action { get; private init; } = string.Empty;
    public string Result { get; private init; } = string.Empty;

    /// <summary>How many values of each entity type were protected, for example PHONE = 2.</summary>
    public IReadOnlyDictionary<string, int> Entities { get; private init; } = new Dictionary<string, int>();

    public long? LatencyMs { get; private init; }
    public string? Model { get; private init; }
    public string? Provider { get; private init; }
    public string? PolicyVersion { get; private init; }

    /// <summary>Why a call was denied or failed, as a stable code such as policy.blocked.</summary>
    public string? Reason { get; private init; }

    public static Result<GatewayRequestEvent> Create(
        string requestId, DateTimeOffset occurredAt, Guid tenantId, Guid clientId, string actorType, string action, string result,
        IReadOnlyDictionary<string, int>? entities = null, long? latencyMs = null,
        string? model = null, string? provider = null, string? policyVersion = null, string? reason = null)
    {
        if (requestId.Length is < 1 or > 40) return Error.Validation("validation.failed", "Request id must be 1 to 40 characters.");
        if (actorType is not ("user" or "client")) return Error.Validation("validation.failed", "Actor type must be user or client.");
        if (!Actions.Contains(action)) return Error.Validation("validation.failed", $"'{action}' is not a gateway action.");
        if (!Results.Contains(result)) return Error.Validation("validation.failed", $"'{result}' is not a result.");
        if (latencyMs is < 0) return Error.Validation("validation.failed", "Latency cannot be negative.");

        entities ??= new Dictionary<string, int>();
        foreach (var (type, count) in entities)
        {
            if (type.Length is < 1 or > 40) return Error.Validation("validation.failed", "Entity type must be 1 to 40 characters.");
            if (count < 0) return Error.Validation("validation.failed", "An entity count cannot be negative.");
        }

        foreach (var text in new[] { model, provider, policyVersion, reason })
            if (text is { Length: > MaxTextLength }) return Error.Validation("validation.failed", "A text field is too long.");

        return new GatewayRequestEvent
        {
            RequestId = requestId, OccurredAt = occurredAt.ToUniversalTime(), TenantId = tenantId, ClientId = clientId,
            ActorType = actorType, Action = action, Result = result, Entities = new Dictionary<string, int>(entities),
            LatencyMs = latencyMs, Model = model, Provider = provider, PolicyVersion = policyVersion, Reason = reason,
        };
    }
}
