using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RefreshTokensTests
{
    [Fact]
    public void Tokens_are_43_url_safe_characters()
        => RefreshTokens.Generate().Should().MatchRegex("^[A-Za-z0-9_-]{43}$");

    [Fact]
    public void Tokens_are_different_every_time()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => RefreshTokens.Generate()).ToList();

        tokens.Distinct().Should().HaveCount(50);
    }

    [Fact]
    public void Hash_is_64_lowercase_hex_and_repeatable()
    {
        var token = RefreshTokens.Generate();

        RefreshTokens.Hash(token).Should().MatchRegex("^[0-9a-f]{64}$");
        RefreshTokens.Hash(token).Should().Be(RefreshTokens.Hash(token));
    }

    [Fact]
    public void Different_tokens_have_different_hashes()
        => RefreshTokens.Hash(RefreshTokens.Generate()).Should().NotBe(RefreshTokens.Hash(RefreshTokens.Generate()));

    [Fact]
    public void The_hash_is_not_the_token()
    {
        var token = RefreshTokens.Generate();

        RefreshTokens.Hash(token).Should().NotContain(token);
    }
}