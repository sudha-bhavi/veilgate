using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Messaging;

/// <summary>
/// The consumer used with the in-memory transport: there is no broker to read from, so it waits until the host stops.
/// Messages sent in memory are never delivered to a consumer, which is fine for running the app without Kafka.
/// </summary>
public sealed class IdleEventConsumer(ILogger<IdleEventConsumer> logger) : IEventConsumer
{
    public async Task ConsumeAsync(string group, IReadOnlyCollection<string> topics,
        Func<ConsumedMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        logger.LogWarning("Messaging:Transport is InMemory, so consumer group {Group} reads nothing", group);
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
