using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Messaging;

/// <summary>
/// Reads from Kafka as part of a consumer group. Kafka remembers, per group and partition, the offset (position) the group has
/// reached. Offsets are committed by hand, one message at a time and only after the handler succeeded, so a crash can only
/// cause a message to be delivered again, never skipped. The handler therefore has to cope with repeats (see GatewayEventProjector).
/// </summary>
public sealed class KafkaEventConsumer(IOptions<KafkaOptions> options, ILogger<KafkaEventConsumer> logger) : IEventConsumer
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    public async Task ConsumeAsync(string group, IReadOnlyCollection<string> topics,
        Func<ConsumedMessage, CancellationToken, Task> handler, CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = group,
            EnableAutoCommit = false,                      // we commit ourselves, after the work is done
            AutoOffsetReset = AutoOffsetReset.Earliest,    // a group that has never read starts at the beginning of the topic
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topics);
        logger.LogInformation("Consumer group {Group} subscribed to {Topics}", group, string.Join(",", topics));

        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try { result = consumer.Consume(PollTimeout); }
                catch (ConsumeException ex)
                {
                    logger.LogWarning("Kafka consume error: {ErrorCode}", ex.Error.Code);   // the kind only
                    await Task.Delay(RetryDelay.ForFailure(++failures, MaxRetryDelay), ct);
                    continue;
                }
                if (result is null) continue;   // nothing arrived within the poll time

                try
                {
                    await handler(ToMessage(result), ct);
                    consumer.Commit(result);   // only now does the group's position move past this message
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Go back to this message so it is read again, and wait a little first (for example the database is down).
                    logger.LogError("Handling {Topic} offset {Offset} failed: {ErrorType}", result.Topic, result.Offset.Value, ex.GetType().Name);
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(RetryDelay.ForFailure(++failures, MaxRetryDelay), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            consumer.Close();   // leave the group cleanly so its partitions are handed over at once
        }
    }

    public static ConsumedMessage ToMessage(ConsumeResult<string, string> result)
    {
        var headers = new Dictionary<string, string>();
        if (result.Message.Headers is { } source)
            foreach (var header in source)
                headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());

        return new ConsumedMessage(result.Topic, result.Message.Key ?? string.Empty, result.Message.Value ?? string.Empty, headers);
    }
}
