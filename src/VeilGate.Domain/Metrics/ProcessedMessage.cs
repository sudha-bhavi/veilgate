namespace VeilGate.Domain.Metrics;

/// <summary>
/// A message a consumer has already handled. Kafka can deliver a message more than once, so a consumer records the message id
/// in the same transaction as its work; a second delivery finds the row and is skipped.
/// </summary>
public sealed class ProcessedMessage
{
    /// <summary>Which consumer handled it. Two consumers can each handle the same message once.</summary>
    public string Consumer { get; init; } = string.Empty;

    public Guid MessageId { get; init; }
    public Guid TenantId { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}
