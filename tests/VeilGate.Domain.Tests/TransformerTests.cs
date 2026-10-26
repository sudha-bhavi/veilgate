using FluentAssertions;
using VeilGate.Application.Policies;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using VeilGate.Domain.Vault;
using Xunit;

namespace VeilGate.Domain.Tests;

public class TransformerTests
{
    private static PolicyDecision Decide(string text, params (string Type, string Value, PolicyAction Action)[] findings)
    {
        var items = findings.Select(f =>
        {
            var start = text.IndexOf(f.Value, StringComparison.Ordinal);
            var entity = new DetectedEntity(f.Type, start, start + f.Value.Length, 1.0, "test");
            return new DecidedEntity(entity, f.Action, 365);
        }).ToList();
        return new PolicyDecision(items);
    }

    [Fact]
    public void Mask_replaces_a_value_with_its_type_label()
    {
        var text = "Call 513-555-0142 now";
        var result = new Transformer().Apply(text, Decide(text, ("PHONE", "513-555-0142", PolicyAction.Mask)), new VaultSession());

        result.Text.Should().Be("Call [PHONE] now");
    }

    [Fact]
    public void Redact_replaces_a_value_with_redacted()
    {
        var text = "Lives at 12 Oak St.";
        var result = new Transformer().Apply(text, Decide(text, ("ADDRESS", "12 Oak St", PolicyAction.Redact)), new VaultSession());

        result.Text.Should().Be("Lives at [REDACTED].");
    }

    [Fact]
    public void Tokenize_uses_a_token_and_remembers_the_original()
    {
        var text = "Patient John Smith arrived";
        var session = new VaultSession();
        var result = new Transformer().Apply(text, Decide(text, ("PERSON", "John Smith", PolicyAction.Tokenize)), session);

        var token = result.Entities.Single().Token!;
        token.Should().MatchRegex(@"^\[\[PERSON_[A-Z2-7]{4}\]\]$");
        result.Text.Should().Be($"Patient {token} arrived");
        session.Tokens[token].Should().Be("John Smith");
    }

    [Fact]
    public void The_same_value_gets_the_same_token_within_a_session()
    {
        var session = new VaultSession();

        session.TokenFor("PERSON", "John Smith").Should().Be(session.TokenFor("PERSON", "John Smith"));
        session.TokenFor("PERSON", "Mary Jones").Should().NotBe(session.TokenFor("PERSON", "John Smith"));
    }

    [Fact]
    public void Allow_leaves_the_text_unchanged()
    {
        var text = "Visit https://acme.test today";
        var result = new Transformer().Apply(text, Decide(text, ("URL", "https://acme.test", PolicyAction.Allow)), new VaultSession());

        result.Text.Should().Be(text);
    }

    [Fact]
    public void Several_findings_are_replaced_in_order_and_the_output_positions_are_correct()
    {
        var text = "Mail a@b.test or call 513-555-0142.";
        var result = new Transformer().Apply(text,
            Decide(text, ("EMAIL", "a@b.test", PolicyAction.Mask), ("PHONE", "513-555-0142", PolicyAction.Mask)),
            new VaultSession());

        result.Text.Should().Be("Mail [EMAIL] or call [PHONE].");
        var phone = result.Entities.Single(e => e.Type == "PHONE");
        result.Text[phone.OutStart..phone.OutEnd].Should().Be("[PHONE]");
    }

    [Fact]
    public void A_block_action_must_never_reach_the_transformer()
    {
        var text = "SSN 123-45-6789";
        var act = () => new Transformer().Apply(text, Decide(text, ("SSN", "123-45-6789", PolicyAction.Block)), new VaultSession());

        act.Should().Throw<InvalidOperationException>();
    }
}