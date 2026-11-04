namespace VeilGate.Application.Abstractions;

/// <summary>One message as a consumer sees it. The headers carry messageId, tenantId, schemaVersion and traceparent.</summary>
public sealed record ConsumedMessage(string Topic, string Key, string Payload, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Where messages are read from. Like <see cref="IEventTransport"/> for sending, this keeps Kafka out of the code that
/// decides what a message means, so the transport can be swapped.
/// </summary>
public interface IEventConsumer
{
    /// <summary>
    /// Reads the topics as a member of the named group and gives each message to the handler, in order within a partition,
    /// until cancelled. When the handler returns, the message counts as done and the group's position moves past it.
    /// When the handler throws, the position does not move: the same message is given to the handler again after a pause.
    /// A message the handler can never process must therefore be dealt with inside the handler (for example sent to a dead-letter
    /// topic) and the handler must then return normally, or it would block every message behind it.
    /// </summary>
    Task ConsumeAsync(string group, IReadOnlyCollection<string> topics, Func<ConsumedMessage, CancellationToken, Task> handler, CancellationToken ct);
}
