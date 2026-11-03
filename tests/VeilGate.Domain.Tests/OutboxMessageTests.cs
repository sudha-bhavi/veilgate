using FluentAssertions;
using VeilGate.Domain.Messaging;
using Xunit;

namespace VeilGate.Domain.Tests;

public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_message_is_unsent_and_due_now()
    {
        var tenant = Guid.NewGuid();
        var m = OutboxMessage.Create(tenant, Now, OutboxTopics.GatewayRequests, """{"requestId":"req_1"}""").Value;

        m.SentAt.Should().BeNull();
        m.Attempts.Should().Be(0);
        m.NextAttemptAt.Should().Be(m.CreatedAt);
        m.LastErrorCode.Should().BeNull();
        m.Key.Should().Be(tenant.ToString());
    }

    [Fact]
    public void The_headers_carry_the_message_id_tenant_and_schema_version()
    {
        var tenant = Guid.NewGuid();
        var m = OutboxMessage.Create(tenant, Now, OutboxTopics.GatewayRequests, "{}").Value;

        m.Headers["messageId"].Should().Be(m.Id.ToString());
        m.Headers["tenantId"].Should().Be(tenant.ToString());
        m.Headers["schemaVersion"].Should().Be("1");
        m.Headers.Should().NotContainKey("traceparent");
    }

    [Fact]
    public void A_trace_parent_is_added_to_the_headers_when_given()
    {
        var m = OutboxMessage.Create(Guid.NewGuid(), Now, OutboxTopics.GatewayRequests, "{}", "00-abc-def-01").Value;
        m.Headers["traceparent"].Should().Be("00-abc-def-01");
    }

    [Fact]
    public void An_unknown_topic_is_refused()
    {
        OutboxMessage.Create(Guid.NewGuid(), Now, "vg.other.v1", "{}").IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":")]
    public void A_payload_that_is_not_json_is_refused(string payload)
    {
        OutboxMessage.Create(Guid.NewGuid(), Now, OutboxTopics.GatewayRequests, payload).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_payload_that_is_too_long_is_refused()
    {
        var payload = "{\"a\":\"" + new string('x', OutboxMessage.MaxPayloadChars) + "\"}";
        OutboxMessage.Create(Guid.NewGuid(), Now, OutboxTopics.GatewayRequests, payload).IsFailure.Should().BeTrue();
    }
}
