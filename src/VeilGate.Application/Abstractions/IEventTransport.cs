using VeilGate.Domain.Common;

namespace VeilGate.Application.Abstractions;

/// <summary>One message ready to leave the system. The headers carry messageId, tenantId, schemaVersion and traceparent.</summary>
public sealed record TransportMessage(string Topic, string Key, string Payload, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Where outbox messages are sent. The publisher talks only to this, so the real transport (Kafka) and a stand-in
/// (in memory, for tests and for running without Kafka) can be swapped without touching the publisher.
/// </summary>
public interface IEventTransport
{
    /// <summary>
    /// Success means the transport has taken responsibility for the message (for Kafka: the broker confirmed it was written).
    /// A failure is returned, not thrown, with a short code that is safe to store; the caller decides whether to retry.
    /// </summary>
    Task<Result> PublishAsync(TransportMessage message, CancellationToken ct = default);
}
