using System.Text.Json;
using FluentAssertions;
using VeilGate.Domain.Messaging;
using Xunit;

namespace VeilGate.Domain.Tests;

public class GatewayRequestEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private static Common.Result<GatewayRequestEvent> Make(
        string requestId = "req_1a2b3c4d", string actorType = "client", string action = "gateway.chat", string result = "success",
        Dictionary<string, int>? entities = null, long? latencyMs = 120, string? reason = null) =>
        GatewayRequestEvent.Create(requestId, Now, Guid.NewGuid(), Guid.NewGuid(), actorType, action, result, entities, latencyMs,
            model: "mock-1", provider: "mock", policyVersion: "3", reason: reason);

    [Fact]
    public void A_valid_event_keeps_what_it_was_given()
    {
        var e = Make(entities: new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 }).Value;

        e.Action.Should().Be("gateway.chat");
        e.Result.Should().Be("success");
        e.Entities.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 });
        e.LatencyMs.Should().Be(120);
        e.Model.Should().Be("mock-1");
    }

    [Theory]
    [InlineData("gateway.detect")]
    [InlineData("gateway.deidentify")]
    [InlineData("gateway.reidentify")]
    [InlineData("gateway.chat")]
    public void Every_gateway_action_is_accepted(string action) => Make(action: action).IsSuccess.Should().BeTrue();

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("denied")]
    public void Every_result_is_accepted(string result) => Make(result: result).IsSuccess.Should().BeTrue();

    [Fact]
    public void An_unknown_action_is_refused() => Make(action: "policy.publish").IsFailure.Should().BeTrue();

    [Fact]
    public void An_unknown_result_is_refused() => Make(result: "maybe").IsFailure.Should().BeTrue();

    [Fact]
    public void An_unknown_actor_type_is_refused() => Make(actorType: "robot").IsFailure.Should().BeTrue();

    [Fact]
    public void An_empty_request_id_is_refused() => Make(requestId: "").IsFailure.Should().BeTrue();

    [Fact]
    public void A_negative_count_is_refused() => Make(entities: new Dictionary<string, int> { ["PHONE"] = -1 }).IsFailure.Should().BeTrue();

    [Fact]
    public void A_negative_latency_is_refused() => Make(latencyMs: -1).IsFailure.Should().BeTrue();

    [Fact]
    public void A_reason_that_is_too_long_is_refused() => Make(reason: new string('x', 201)).IsFailure.Should().BeTrue();

    [Fact]
    public void The_time_is_kept_in_utc()
    {
        var local = new DateTimeOffset(2026, 11, 3, 15, 30, 0, TimeSpan.FromHours(5.5));
        var e = GatewayRequestEvent.Create("req_1", local, Guid.NewGuid(), Guid.NewGuid(), "user", "gateway.detect", "success").Value;

        e.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
        e.OccurredAt.Should().Be(local);
    }

    [Fact]
    public void It_serializes_to_the_json_the_consumer_will_read()
    {
        var e = Make(entities: new Dictionary<string, int> { ["PHONE"] = 2 }).Value;

        var json = JsonSerializer.Serialize(e, JsonSerializerOptions.Web);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("requestId").GetString().Should().Be("req_1a2b3c4d");
        doc.RootElement.GetProperty("action").GetString().Should().Be("gateway.chat");
        doc.RootElement.GetProperty("entities").GetProperty("PHONE").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("latencyMs").GetInt64().Should().Be(120);
    }
}
