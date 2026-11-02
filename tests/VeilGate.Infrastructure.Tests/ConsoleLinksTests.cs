using FluentAssertions;
using Microsoft.Extensions.Options;
using VeilGate.Infrastructure.Email;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

public class ConsoleLinksTests
{
    private static ConsoleLinks With(string baseUrl) => new(Options.Create(new ConsoleOptions { BaseUrl = baseUrl }));

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:5173/")]
    public void The_link_points_at_the_accept_page_with_the_token(string baseUrl)
        => With(baseUrl).AcceptInvitation("abc-DEF_123").Should().Be("http://localhost:5173/accept-invite?token=abc-DEF_123");

    [Fact]
    public void A_missing_base_url_fails_loudly()
    {
        var act = () => With("").AcceptInvitation("abc");
        act.Should().Throw<InvalidOperationException>();
    }
}