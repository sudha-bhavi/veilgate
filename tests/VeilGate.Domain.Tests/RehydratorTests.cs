using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Vault;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RehydratorTests
{
    private static Rehydrator With(params (string Token, string Value)[] known)
    {
        var vault = Substitute.For<IVaultService>();
        vault.ResolveAsync(Arg.Any<VaultSession>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => known.FirstOrDefault(k => k.Token == call.ArgAt<string>(1)).Value);
        return new Rehydrator(vault);
    }

    [Fact]
    public async Task Known_tokens_are_replaced_everywhere_they_appear()
    {
        var r = With(("[[PERSON_7F3A]]", "John Smith"));

        var result = await r.RestoreAsync("[[PERSON_7F3A]] said hi. Thanks, [[PERSON_7F3A]]!", new VaultSession());

        result.Text.Should().Be("John Smith said hi. Thanks, John Smith!");
        result.Replaced.Should().Be(2);
        result.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_tokens_stay_as_they_are_and_are_reported()
    {
        var r = With(("[[PERSON_7F3A]]", "John Smith"));

        var result = await r.RestoreAsync("[[PERSON_7F3A]] met [[PERSON_ZZZ2]]", new VaultSession());

        result.Text.Should().Be("John Smith met [[PERSON_ZZZ2]]");
        result.Unresolved.Should().Equal("[[PERSON_ZZZ2]]");
    }

    [Fact]
    public async Task Text_without_tokens_is_returned_untouched_and_the_vault_is_not_asked()
    {
        var vault = Substitute.For<IVaultService>();

        var result = await new Rehydrator(vault).RestoreAsync("Nothing [to] see [[here]]", new VaultSession());

        result.Text.Should().Be("Nothing [to] see [[here]]");
        await vault.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default);
    }
}
