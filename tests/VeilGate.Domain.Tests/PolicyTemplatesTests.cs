using FluentAssertions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class PolicyTemplatesTests
{
    [Theory]
    [InlineData("blank")]
    [InlineData("hipaa-safe-harbor")]
    [InlineData("names-only")]
    public void Every_template_is_valid_without_errors_or_warnings(string name)
    {
        var result = PolicyParser.Parse(PolicyTemplates.YamlOf(name));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void The_three_documented_templates_exist()
    {
        PolicyTemplates.Names.Should().BeEquivalentTo("blank", "hipaa-safe-harbor", "names-only");
        PolicyTemplates.Get("nope").Should().BeNull();
    }

    [Fact]
    public void Hipaa_safe_harbor_follows_the_database_doc()
    {
        var p = PolicyTemplates.Get("hipaa-safe-harbor")!;

        p.RuleFor("PERSON").Action.Should().Be(PolicyAction.Tokenize);
        p.RuleFor("ADDRESS").Action.Should().Be(PolicyAction.Tokenize);
        p.RuleFor("PHONE").Action.Should().Be(PolicyAction.Mask);
        p.RuleFor("EMAIL").Action.Should().Be(PolicyAction.Mask);
        p.RuleFor("SSN").Action.Should().Be(PolicyAction.Redact);
        p.RuleFor("DATE").Action.Should().Be(PolicyAction.Shift);
        p.RuleFor("URL").Action.Should().Be(PolicyAction.Redact, "unlisted types fall back to REDACT");
        p.ReidentifyEnabled.Should().BeTrue();
    }

    [Fact]
    public void Names_only_lets_everything_else_through()
    {
        var p = PolicyTemplates.Get("names-only")!;

        p.RuleFor("PERSON").Action.Should().Be(PolicyAction.Tokenize);
        p.RuleFor("PHONE").Action.Should().Be(PolicyAction.Allow);
    }

    [Fact]
    public void Blank_hides_everything()
    {
        var p = PolicyTemplates.Get("blank")!;

        p.RuleFor("PERSON").Action.Should().Be(PolicyAction.Redact);
        p.ReidentifyEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task The_built_in_resolver_serves_hipaa_safe_harbor_and_refuses_unknown_policy_ids()
    {
        var resolver = new BuiltInPolicyResolver();

        var ok = await resolver.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), null);
        var missing = await resolver.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        ok.IsSuccess.Should().BeTrue();
        ok.Value.Document.Name.Should().Be("hipaa-safe-harbor-v1");
        ok.Value.Version.Should().Be(1);
        missing.Error.Code.Should().Be("resource.not_found");
    }
}
