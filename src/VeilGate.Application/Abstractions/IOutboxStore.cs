namespace VeilGate.Application.Abstractions;

/// <summary>A message the publisher has taken from the outbox. Attempts includes the attempt being made now.</summary>
public sealed record ClaimedMessage(
    Guid Id, Guid TenantId, string Topic, string Key, string Payload,
    IReadOnlyDictionary<string, string> Headers, int Attempts, DateTimeOffset CreatedAt);

/// <summary>How the publisher takes messages from the outbox and records what happened to them. It works across all tenants.</summary>
public interface IOutboxStore
{
    /// <summary>Takes up to <paramref name="limit"/> due messages. Each is hidden from other publishers for the length of the lease.</summary>
    Task<IReadOnlyList<ClaimedMessage>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken ct = default);

    Task MarkSentAsync(Guid id, CancellationToken ct = default);

    /// <summary>Keeps the message for another try after a wait that doubles with each attempt, up to <paramref name="backoffCap"/>.</summary>
    Task MarkFailedAsync(Guid id, string errorCode, TimeSpan backoffCap, CancellationToken ct = default);

    /// <summary>Finishes a message that was put on the dead-letter topic. The error code stays on the row.</summary>
    Task MarkDeadAsync(Guid id, string errorCode, CancellationToken ct = default);
}
