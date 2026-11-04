using System.Text.Json;
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

    /// <summary>
    /// Reads an event from the JSON that was published, applying the same rules as <see cref="Create"/>. Anything that is not a
    /// valid event is a failure: a consumer treats it as a message it can never handle.
    /// </summary>
    public static Result<GatewayRequestEvent> Parse(string json)
    {
        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return Error.Validation("validation.failed", "The message is not valid JSON."); }

        if (wire?.RequestId is null || wire.ActorType is null || wire.Action is null || wire.Result is null || wire.TenantId == Guid.Empty)
            return Error.Validation("validation.failed", "The message is missing required fields.");

        return Create(wire.RequestId, wire.OccurredAt, wire.TenantId, wire.ClientId, wire.ActorType, wire.Action, wire.Result,
            wire.Entities, wire.LatencyMs, wire.Model, wire.Provider, wire.PolicyVersion, wire.Reason);
    }

    // The shape on the wire. Kept apart from the event so the event itself cannot be built without going through Create.
    private sealed class Wire
    {
        public string? RequestId { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public Guid TenantId { get; set; }
        public Guid ClientId { get; set; }
        public string? ActorType { get; set; }
        public string? Action { get; set; }
        public string? Result { get; set; }
        public Dictionary<string, int>? Entities { get; set; }
        public long? LatencyMs { get; set; }
        public string? Model { get; set; }
        public string? Provider { get; set; }
        public string? PolicyVersion { get; set; }
        public string? Reason { get; set; }
    }
}
