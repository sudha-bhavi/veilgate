using System.Collections.Concurrent;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;

namespace VeilGate.Infrastructure.Messaging;

/// <summary>
/// Keeps messages in memory instead of sending them anywhere. Used by tests, and usable in development when Kafka is not running.
/// Set <see cref="FailWith"/> to make every publish fail, which is how tests exercise retries.
/// </summary>
public sealed class InMemoryEventTransport : IEventTransport
{
    private readonly ConcurrentQueue<TransportMessage> _published = new();

    public IReadOnlyCollection<TransportMessage> Published => _published.ToArray();

    public Error? FailWith { get; set; }

    public Task<Result> PublishAsync(TransportMessage message, CancellationToken ct = default)
    {
        if (FailWith is { } error) return Task.FromResult(Result.Failure(error));
        _published.Enqueue(message);
        return Task.FromResult(Result.Success());
    }
}
