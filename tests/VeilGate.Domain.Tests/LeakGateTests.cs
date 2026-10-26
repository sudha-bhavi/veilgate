using FluentAssertions;
using VeilGate.Application.Detection;
using VeilGate.Application.Policies;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Policies;
using VeilGate.Domain.Vault;
using VeilGate.Infrastructure.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class LeakGateTests
{
    private static DetectionPipeline Pipeline() => new([new RegexDetector()]);
    private static OutboundLeakGate Gate() => new(Pipeline());

    private static readonly PolicyDocument Policy = new()
    {
        Name = "test",
        MinConfidence = 0.5,
        Entities = new Dictionary<string, EntityRule> { ["URL"] = new(PolicyAction.Allow) },
    };

    private static TransformResult Clean(string text, params TransformedEntity[] entities) => new(text, entities);

    [Fact]
    public async Task Clean_text_passes()
    {
        var result = await Gate().CheckAsync(Clean("Patient was seen and is doing well."), Policy, DetectionContext.Default);

        result.Tripped.Should().BeFalse();
    }

    [Fact]
    public async Task A_value_that_survived_transformation_trips_the_gate()
    {
        var result = await Gate().CheckAsync(Clean("Call 513-555-0142 now"), Policy, DetectionContext.Default);

        result.Tripped.Should().BeTrue();
        result.LeakedTypes.Should().Equal("PHONE");
    }

    [Fact]
    public async Task Our_own_mask_labels_and_tokens_do_not_trip_the_gate()
    {
        var result = await Gate().CheckAsync(Clean("Call [PHONE] about [[PERSON_7F3A]] at [REDACTED]"), Policy, DetectionContext.Default);

        result.Tripped.Should().BeFalse();
    }

    [Fact]
    public async Task A_shifted_date_does_not_trip_the_gate()
    {
        // "04/09/2025" sits at positions 8..18 of the output and was written by the date shifter.
        var shifted = new TransformedEntity("DATE", 8, 18, PolicyAction.Shift, null, OutStart: 8, OutEnd: 18);

        var result = await Gate().CheckAsync(Clean("Seen on 04/09/2025.", shifted), Policy, DetectionContext.Default);

        result.Tripped.Should().BeFalse();
    }

    [Fact]
    public async Task The_same_date_outside_a_shifted_span_does_trip_the_gate()
    {
        var result = await Gate().CheckAsync(Clean("Seen on 04/09/2025."), Policy, DetectionContext.Default);

        result.Tripped.Should().BeTrue();
        result.LeakedTypes.Should().Equal("DATE");
    }

    [Fact]
    public async Task A_type_the_policy_allows_does_not_trip_the_gate()
    {
        var result = await Gate().CheckAsync(Clean("See https://acme.test/docs"), Policy, DetectionContext.Default);

        result.Tripped.Should().BeFalse();
    }

    [Fact]
    public async Task The_whole_flow_detect_decide_transform_check()
    {
        var policy = PolicyParser.Parse("""
            version: 1
            name: Flow
            defaults: { action: REDACT }
            entities:
              PHONE: { action: MASK }
              EMAIL: { action: TOKENIZE }
            """).Policy!;
        var text = "Call 513-555-0142 or mail a@b.test";

        var found = await Pipeline().DetectAsync(text, DetectionContext.Default);
        var decision = new PolicyEngine().Decide(found, policy);
        var session = new VaultSession();
        var transformed = new Transformer().Apply(text, decision, session);
        var gate = await Gate().CheckAsync(transformed, policy, DetectionContext.Default);

        transformed.Text.Should().StartWith("Call [PHONE] or mail [[EMAIL_");
        transformed.Text.Should().NotContain("513-555-0142").And.NotContain("a@b.test");
        session.Tokens.Values.Should().Contain("a@b.test");
        gate.Tripped.Should().BeFalse();
    }
}