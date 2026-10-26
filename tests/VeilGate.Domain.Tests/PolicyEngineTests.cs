using FluentAssertions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class PolicyEngineTests
{
    private static readonly PolicyDocument Policy = new()
    {
        Name = "test",
        DefaultAction = PolicyAction.Redact,
        MinConfidence = 0.5,
        Entities = new Dictionary<string, EntityRule>
        {
            ["PHONE"] = new(PolicyAction.Mask),
            ["SSN"] = new(PolicyAction.Block),
            ["DATE"] = new(PolicyAction.Shift, 30),
        },
    };

    private static DetectedEntity Found(string type, double confidence = 1.0) => new(type, 0, 5, confidence, "regex");

    [Fact]
    public void Uses_the_action_listed_for_the_type()
    {
        var d = new PolicyEngine().Decide([Found("PHONE")], Policy);

        d.Items.Single().Action.Should().Be(PolicyAction.Mask);
    }

    [Fact]
    public void Falls_back_to_the_default_for_unlisted_types()
    {
        var d = new PolicyEngine().Decide([Found("URL")], Policy);

        d.Items.Single().Action.Should().Be(PolicyAction.Redact);
    }

    [Fact]
    public void A_block_action_blocks_the_whole_request_and_names_the_type()
    {
        var d = new PolicyEngine().Decide([Found("PHONE"), Found("SSN")], Policy);

        d.IsBlocked.Should().BeTrue();
        d.BlockedTypes.Should().Equal("SSN");
    }

    [Fact]
    public void Findings_below_the_policy_minimum_confidence_are_dropped()
    {
        var d = new PolicyEngine().Decide([Found("PHONE", confidence: 0.3)], Policy);

        d.Items.Should().BeEmpty();
    }

    [Fact]
    public void Shift_carries_the_max_days_from_the_policy()
    {
        var d = new PolicyEngine().Decide([Found("DATE")], Policy);

        d.Items.Single().MaxDays.Should().Be(30);
    }
}