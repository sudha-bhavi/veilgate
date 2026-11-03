using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using DomainError = VeilGate.Domain.Common.Error;

namespace VeilGate.Infrastructure.Messaging;

public sealed class OutboxPublisherOptions
{
    public const string Section = "Outbox";

    public int PollMilliseconds { get; set; } = 500;
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a claimed message stays hidden from other publishers. If this one crashes, the message is tried again after this.</summary>
    public int LeaseSeconds { get; set; } = 60;

    /// <summary>After this many failed attempts a message goes to the dead-letter topic instead of being retried.</summary>
    public int MaxAttempts { get; set; } = 5;

    public int BackoffCapSeconds { get; set; } = 300;
}

/// <summary>
/// Takes a batch of due messages from the outbox and sends them. Delivery is "at least once": if the process dies after the
/// transport accepted a message but before it was marked sent, the message is sent again. Consumers must cope with repeats
/// (the messageId header identifies a message).
/// </summary>
public sealed class OutboxPublishProcessor(
    IServiceScopeFactory scopes, IEventTransport transport, IOptions<OutboxPublisherOptions> options, ILogger<OutboxPublishProcessor> logger)
{
    public const string DeadLetterSuffix = ".dlq";

    /// <summary>Returns how many messages were taken, so the caller knows whether to poll again at once or wait.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var claimed = await store.ClaimDueAsync(o.BatchSize, TimeSpan.FromSeconds(o.LeaseSeconds), ct);
        foreach (var message in claimed.OrderBy(m => m.CreatedAt))
        {
            ct.ThrowIfCancellationRequested();
            await PublishOneAsync(store, message, o, ct);
        }
        return claimed.Count;
    }

    private async Task PublishOneAsync(IOutboxStore store, ClaimedMessage message, OutboxPublisherOptions o, CancellationToken ct)
    {
        var sent = await SendAsync(new TransportMessage(message.Topic, message.Key, message.Payload, message.Headers), ct);
        if (sent.IsSuccess)
        {
            await store.MarkSentAsync(message.Id, ct);
            return;
        }

        var errorCode = sent.Error.Code;
        if (message.Attempts >= o.MaxAttempts)
        {
            var headers = new Dictionary<string, string>(message.Headers) { ["x-error"] = errorCode, ["x-attempts"] = message.Attempts.ToString() };
            var dead = await SendAsync(new TransportMessage(message.Topic + DeadLetterSuffix, message.Key, message.Payload, headers), ct);
            if (dead.IsSuccess)
            {
                logger.LogError("Outbox message {MessageId} moved to the dead-letter topic after {Attempts} attempts: {ErrorCode}",
                    message.Id, message.Attempts, errorCode);
                await store.MarkDeadAsync(message.Id, errorCode, ct);
                return;
            }
            errorCode = dead.Error.Code;   // even the dead-letter topic is unreachable: keep trying, do not lose the message
        }

        logger.LogWarning("Outbox message {MessageId} failed on attempt {Attempts}: {ErrorCode}", message.Id, message.Attempts, errorCode);
        await store.MarkFailedAsync(message.Id, errorCode, TimeSpan.FromSeconds(o.BackoffCapSeconds), ct);
    }

    private async Task<Result> SendAsync(TransportMessage message, CancellationToken ct)
    {
        try { return await transport.PublishAsync(message, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Transport threw {ErrorType}", ex.GetType().Name);   // the type only, never a message
            return Result.Failure(new DomainError("transport.error", "The message could not be sent."));
        }
    }
}
