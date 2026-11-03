using FluentAssertions;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Messaging;
using Xunit;

namespace VeilGate.Domain.Tests;

public class InMemoryEventTransportTests
{
    private static TransportMessage Message(string key = "tenant-a") =>
        new("vg.gateway.requests.v1", key, "{}", new Dictionary<string, string> { ["messageId"] = Guid.NewGuid().ToString() });

    [Fact]
    public async Task A_published_message_is_kept_in_order()
    {
        IEventTransport transport = new InMemoryEventTransport();
        var first = Message("a");
        var second = Message("b");

        (await transport.PublishAsync(first)).IsSuccess.Should().BeTrue();
        (await transport.PublishAsync(second)).IsSuccess.Should().BeTrue();

        ((InMemoryEventTransport)transport).Published.Should().Equal(first, second);
    }

    [Fact]
    public async Task A_failing_transport_returns_the_error_and_keeps_nothing()
    {
        var transport = new InMemoryEventTransport { FailWith = new Error("transport.unavailable", "down") };

        var result = await transport.PublishAsync(Message());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("transport.unavailable");
        transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_the_failure_lets_publishing_work_again()
    {
        var transport = new InMemoryEventTransport { FailWith = new Error("transport.unavailable", "down") };
        await transport.PublishAsync(Message());

        transport.FailWith = null;

        (await transport.PublishAsync(Message())).IsSuccess.Should().BeTrue();
        transport.Published.Should().HaveCount(1);
    }
}
