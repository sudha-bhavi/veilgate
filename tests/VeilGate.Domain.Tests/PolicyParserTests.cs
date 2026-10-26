using FluentAssertions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class PolicyParserTests
{
    private const string Valid = """
        version: 1
        name: Default
        defaults:
          action: MASK
          minConfidence: 0.5
        entities:
          PERSON:  { action: TOKENIZE }
          SSN:     { action: BLOCK }
          DATE:    { action: SHIFT, maxDays: 90 }
        reidentify:
          enabled: true
          allowedScopes: [gateway:reidentify]
          ttlSeconds: 1800
        output:
          scan: true
          onLeak: BLOCK
        limits:
          maxTextChars: 1000
        """;

    [Fact]
    public void Parses_the_sample_policy_from_the_design_doc()
    {
        var result = PolicyParser.Parse(Valid);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        var p = result.Policy!;
        p.Name.Should().Be("Default");
        p.DefaultAction.Should().Be(PolicyAction.Mask);
        p.RuleFor("PERSON").Action.Should().Be(PolicyAction.Tokenize);
        p.RuleFor("DATE").MaxDays.Should().Be(90);
        p.ReidentifyEnabled.Should().BeTrue();
        p.ReidentifyTtlSeconds.Should().Be(1800);
        p.OutputOnLeak.Should().Be(PolicyAction.Block);
        p.MaxTextChars.Should().Be(1000);
    }

    [Fact]
    public void An_unknown_action_is_reported_with_its_line_number()
    {
        var result = PolicyParser.Parse("""
            version: 1
            name: Bad
            entities:
              PHONE: { action: MASKK }
            """);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Line == 4 && e.Message.Contains("MASKK"));
    }

    [Fact]
    public void An_unknown_entity_type_is_rejected()
    {
        var result = PolicyParser.Parse("version: 1\nname: Bad\nentities:\n  FOO: { action: MASK }\n");

        result.Errors.Should().ContainSingle(e => e.Message.Contains("FOO"));
    }

    [Fact]
    public void A_misspelled_setting_is_an_error_not_silently_ignored()
    {
        var result = PolicyParser.Parse("version: 1\nname: Bad\ndefaults:\n  minConfidance: 0.9\n");

        result.Errors.Should().ContainSingle(e => e.Message.Contains("minConfidance"));
    }

    [Fact]
    public void Shift_is_only_allowed_for_dates()
    {
        var result = PolicyParser.Parse("version: 1\nname: Bad\nentities:\n  PHONE: { action: SHIFT }\n");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Allow_on_a_hipaa_identifier_is_valid_but_warns()
    {
        var result = PolicyParser.Parse("version: 1\nname: Risky\nentities:\n  SSN: { action: ALLOW }\n");

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.Contains("SSN"));
    }

    [Fact]
    public void Broken_yaml_gives_one_error_instead_of_throwing()
    {
        var result = PolicyParser.Parse("version: 1\n  name: [oops");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void A_missing_name_is_reported()
    {
        PolicyParser.Parse("version: 1\n").Errors.Should().Contain(e => e.Message.Contains("name"));
    }
}