using System.Text.Json;
using VeilGate.Domain.Common;

namespace VeilGate.Domain.Messaging;

/// <summary>The topics the outbox may carry. Each one also has a ".dlq" companion for messages that can never be delivered.</summary>
public static class OutboxTopics
{
    public const string GatewayRequests = "vg.gateway.requests.v1";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { GatewayRequests };
}

/// <summary>
/// A message waiting to be sent to the event transport (Backend doc 6.6). It is saved in the same transaction as the work that
/// caused it, so the work and its message either both exist or neither does. A worker sends it later and marks it sent.
/// Like the audit log it never holds content, only what the consumers need.
/// </summary>
public sealed class OutboxMessage
{
    public const int SchemaVersion = 1;
    public const int MaxPayloadChars = 8000;

    private OutboxMessage() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public string Topic { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string Payload { get; private set; } = "{}";
    public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string? LastErrorCode { get; private set; }

    public static Result<OutboxMessage> Create(Guid tenantId, DateTimeOffset now, string topic, string payloadJson, string? traceParent = null)
    {
        if (!OutboxTopics.All.Contains(topic)) return Error.Validation("validation.failed", $"'{topic}' is not a known topic.");
        if (payloadJson.Length is < 2 or > MaxPayloadChars) return Error.Validation("validation.failed", "Payload must be 2 to 8000 characters.");

        try { using var _ = JsonDocument.Parse(payloadJson); }
        catch (JsonException) { return Error.Validation("validation.failed", "Payload must be valid JSON."); }

        var id = Guid.CreateVersion7();
        var headers = new Dictionary<string, string>
        {
            ["messageId"] = id.ToString(),
            ["tenantId"] = tenantId.ToString(),
            ["schemaVersion"] = SchemaVersion.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(traceParent)) headers["traceparent"] = traceParent;

        var utc = now.UtcDateTime;   // PostgreSQL keeps microseconds; cut the extra digit so what we hold equals what is stored
        var created = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        return new OutboxMessage
        {
            Id = id, TenantId = tenantId, Topic = topic,
            Key = tenantId.ToString(),   // the same tenant always lands in the same partition, so its messages stay in order
            Payload = payloadJson, Headers = headers, CreatedAt = created, NextAttemptAt = created,
        };
    }
}
