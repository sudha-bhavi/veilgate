using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Messaging;

/// <summary>
/// What the metrics consumer does with each message: hand it to the projector, and send a message that can never be handled to
/// the topic's dead-letter companion. The consumer only moves past a message when this returns, so it returns normally for
/// a message that was handled or dead-lettered, and throws only when the message should be tried again.
/// </summary>
public sealed class GatewayEventConsumerHandler(IGatewayEventProjector projector, IEventTransport transport, ILogger<GatewayEventConsumerHandler> logger)
{
    public async Task HandleAsync(ConsumedMessage message, CancellationToken ct)
    {
        if (!message.Headers.TryGetValue("messageId", out var raw) || !Guid.TryParse(raw, out var messageId))
        {
            await DeadLetterAsync(message, "consumer.missing_message_id", ct);
            return;
        }

        // A failure here (the database is down, say) is not caught: the consumer reads the same message again after a pause.
        var outcome = await projector.ProjectAsync(messageId, message.Payload, ct);

        switch (outcome)
        {
            case ProjectionOutcome.Applied:
                logger.LogDebug("Message {MessageId} applied", messageId);
                break;
            case ProjectionOutcome.Duplicate:
                logger.LogInformation("Message {MessageId} was already handled; skipped", messageId);
                break;
            default:
                await DeadLetterAsync(message, "consumer.invalid_event", ct);
                break;
        }
    }

    private async Task DeadLetterAsync(ConsumedMessage message, string reason, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(message.Headers) { ["x-error"] = reason };
        var sent = await transport.PublishAsync(
            new TransportMessage(message.Topic + OutboxPublishProcessor.DeadLetterSuffix, message.Key, message.Payload, headers), ct);

        // If even the dead-letter topic cannot take it, do not move on: throwing makes the consumer try this message again.
        if (sent.IsFailure) throw new InvalidOperationException($"Could not dead-letter a message: {sent.Error.Code}");

        logger.LogWarning("A message on {Topic} was dead-lettered: {Reason}", message.Topic, reason);   // never the payload
    }
}
