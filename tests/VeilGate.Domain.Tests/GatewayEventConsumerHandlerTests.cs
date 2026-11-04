using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Messaging;
using Xunit;

namespace VeilGate.Domain.Tests;

public class GatewayEventConsumerHandlerTests
{
    private const string Topic = "vg.gateway.requests.v1";

    private sealed class FakeProjector(Func<ProjectionOutcome> outcome) : IGatewayEventProjector
    {
        public readonly List<(Guid MessageId, string Payload)> Calls = [];

        public Task<ProjectionOutcome> ProjectAsync(Guid messageId, string payloadJson, CancellationToken ct = default)
        {
            Calls.Add((messageId, payloadJson));
            return Task.FromResult(outcome());
        }
    }

    private static (GatewayEventConsumerHandler Handler, FakeProjector Projector, InMemoryEventTransport Transport) Build(Func<ProjectionOutcome>? outcome = null)
    {
        var projector = new FakeProjector(outcome ?? (() => ProjectionOutcome.Applied));
        var transport = new InMemoryEventTransport();
        return (new GatewayEventConsumerHandler(projector, transport, NullLogger<GatewayEventConsumerHandler>.Instance), projector, transport);
    }

    private static ConsumedMessage Message(string? messageId, string payload = "{}") => new(Topic, "tenant-a", payload,
        messageId is null
            ? new Dictionary<string, string> { ["tenantId"] = "tenant-a" }
            : new Dictionary<string, string> { ["messageId"] = messageId, ["tenantId"] = "tenant-a" });

    [Fact]
    public async Task A_valid_message_goes_to_the_projector_and_nowhere_else()
    {
        var (handler, projector, transport) = Build();
        var id = Guid.NewGuid();

        await handler.HandleAsync(Message(id.ToString(), "{\"a\":1}"), CancellationToken.None);

        projector.Calls.Should().ContainSingle().Which.Should().Be((id, "{\"a\":1}"));
        transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_that_was_already_handled_is_skipped_quietly()
    {
        var (handler, projector, transport) = Build(() => ProjectionOutcome.Duplicate);

        await handler.HandleAsync(Message(Guid.NewGuid().ToString()), CancellationToken.None);

        projector.Calls.Should().HaveCount(1);
        transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_that_is_not_a_valid_event_goes_to_the_dead_letter_topic_with_the_reason()
    {
        var (handler, _, transport) = Build(() => ProjectionOutcome.Rejected);
        var id = Guid.NewGuid();

        await handler.HandleAsync(Message(id.ToString(), "{\"hello\":\"kafka\"}"), CancellationToken.None);

        var dead = transport.Published.Should().ContainSingle().Subject;
        dead.Topic.Should().Be(Topic + ".dlq");
        dead.Key.Should().Be("tenant-a");
        dead.Payload.Should().Be("{\"hello\":\"kafka\"}");
        dead.Headers["x-error"].Should().Be("consumer.invalid_event");
        dead.Headers["messageId"].Should().Be(id.ToString());   // the original headers travel along
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task A_message_without_a_usable_message_id_is_dead_lettered_without_reaching_the_projector(string? messageId)
    {
        var (handler, projector, transport) = Build();

        await handler.HandleAsync(Message(messageId), CancellationToken.None);

        projector.Calls.Should().BeEmpty();
        transport.Published.Should().ContainSingle().Which.Headers["x-error"].Should().Be("consumer.missing_message_id");
    }

    [Fact]
    public async Task If_the_dead_letter_topic_cannot_take_the_message_the_handler_throws_so_it_is_tried_again()
    {
        var (handler, _, transport) = Build(() => ProjectionOutcome.Rejected);
        transport.FailWith = new Error("transport.unavailable", "down");

        var act = () => handler.HandleAsync(Message(Guid.NewGuid().ToString()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_failure_in_the_projector_is_not_swallowed_and_nothing_is_dead_lettered()
    {
        var (handler, _, transport) = Build(() => throw new TimeoutException("database down"));

        var act = () => handler.HandleAsync(Message(Guid.NewGuid().ToString()), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        transport.Published.Should().BeEmpty();   // a temporary failure must not turn a good message into a dead letter
    }
}
