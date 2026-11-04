using System.Text;
using Confluent.Kafka;
using FluentAssertions;
using VeilGate.Infrastructure.Messaging;
using Xunit;

namespace VeilGate.Domain.Tests;

public class KafkaConsumerHelpersTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(100, 30)]
    public void The_wait_doubles_with_each_failure_up_to_the_cap(int failures, int seconds)
    {
        RetryDelay.ForFailure(failures, Cap).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void A_consumed_message_keeps_its_topic_key_payload_and_headers()
    {
        var headers = new Headers { { "messageId", Encoding.UTF8.GetBytes("abc") }, { "schemaVersion", Encoding.UTF8.GetBytes("1") } };
        var result = new ConsumeResult<string, string>
        {
            Topic = "vg.gateway.requests.v1",
            Message = new Message<string, string> { Key = "tenant-a", Value = "{\"a\":1}", Headers = headers },
        };

        var message = KafkaEventConsumer.ToMessage(result);

        message.Topic.Should().Be("vg.gateway.requests.v1");
        message.Key.Should().Be("tenant-a");
        message.Payload.Should().Be("{\"a\":1}");
        message.Headers.Should().BeEquivalentTo(new Dictionary<string, string> { ["messageId"] = "abc", ["schemaVersion"] = "1" });
    }

    [Fact]
    public void A_message_without_headers_or_key_gives_empty_values_not_nulls()
    {
        var result = new ConsumeResult<string, string>
        {
            Topic = "t",
            Message = new Message<string, string> { Key = null!, Value = "x" },
        };

        var message = KafkaEventConsumer.ToMessage(result);

        message.Key.Should().BeEmpty();
        message.Headers.Should().BeEmpty();
    }
}
