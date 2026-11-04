using System.Text.Json;
using FluentAssertions;
using VeilGate.Domain.Messaging;
using Xunit;

namespace VeilGate.Domain.Tests;

public class GatewayRequestEventParseTests
{
    private static GatewayRequestEvent Sample() => GatewayRequestEvent.Create(
        "req_1a2b3c4d", new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), Guid.NewGuid(), Guid.NewGuid(), "user", "gateway.chat", "denied",
        new Dictionary<string, int> { ["PHONE"] = 2 }, 120, "gpt-4o-mini", "mock-llm", "7", "policy.blocked").Value;

    [Fact]
    public void An_event_that_was_serialized_can_be_read_back()
    {
        var original = Sample();

        var parsed = GatewayRequestEvent.Parse(JsonSerializer.Serialize(original, JsonSerializerOptions.Web));

        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Json_that_the_database_has_reformatted_can_still_be_read()
    {
        var original = Sample();
        // jsonb puts a space after each colon and may reorder the keys, as the published messages do.
        var json = JsonSerializer.Serialize(original, JsonSerializerOptions.Web).Replace("\":", "\": ");

        GatewayRequestEvent.Parse(json).Value.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("12")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Something_that_is_not_an_event_is_refused(string json)
    {
        GatewayRequestEvent.Parse(json).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void An_event_with_an_unknown_action_is_refused()
    {
        var json = JsonSerializer.Serialize(Sample(), JsonSerializerOptions.Web).Replace("gateway.chat", "policy.publish");

        GatewayRequestEvent.Parse(json).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void An_event_without_a_tenant_is_refused()
    {
        var json = JsonSerializer.Serialize(Sample(), JsonSerializerOptions.Web);
        using var doc = JsonDocument.Parse(json);
        var tenant = doc.RootElement.GetProperty("tenantId").GetString()!;

        GatewayRequestEvent.Parse(json.Replace(tenant, Guid.Empty.ToString())).IsFailure.Should().BeTrue();
    }
}
