using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using DomainError = VeilGate.Domain.Common.Error;   // Confluent.Kafka has its own Error type

namespace VeilGate.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    /// <summary>Where the broker is reached from this process. From your Mac that is localhost:19092 (the "external" listener).</summary>
    public string BootstrapServers { get; set; } = "localhost:19092";

    /// <summary>How long Kafka may keep retrying one message before giving up and reporting a failure.</summary>
    public int MessageTimeoutMs { get; set; } = 10_000;
}

/// <summary>
/// Sends messages to Kafka. The producer waits for every in-sync copy of the broker to confirm (acks=all) and is idempotent:
/// if it retries a send after a network hiccup, Kafka recognises the repeat and does not store it twice.
/// </summary>
public sealed class KafkaEventTransport : IEventTransport, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaEventTransport> _logger;

    public KafkaEventTransport(IOptions<KafkaOptions> options, ILogger<KafkaEventTransport> logger)
    {
        _logger = logger;
        var o = options.Value;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = o.MessageTimeoutMs,
        }).Build();
    }

    public async Task<Result> PublishAsync(TransportMessage message, CancellationToken ct = default)
    {
        var headers = new Headers();
        foreach (var (name, value) in message.Headers) headers.Add(name, Encoding.UTF8.GetBytes(value));

        try
        {
            await _producer.ProduceAsync(message.Topic,
                new Message<string, string> { Key = message.Key, Value = message.Payload, Headers = headers }, ct);
            return Result.Success();
        }
        catch (ProduceException<string, string> ex)
        {
            // The kind of failure only, never the message: it is stored in last_error_code and logged.
            var code = $"transport.{ex.Error.Code}";
            _logger.LogWarning("Kafka publish failed on {Topic}: {ErrorCode}", message.Topic, code);
            return Result.Failure(new DomainError(code, "The message could not be sent."));
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));   // let anything still in flight finish
        _producer.Dispose();
    }
}
