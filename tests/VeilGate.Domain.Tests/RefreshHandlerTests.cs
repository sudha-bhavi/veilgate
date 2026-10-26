using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RefreshHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();

    public RefreshHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _tokens.IssueAccessToken(Arg.Any<AccessTokenClaims>(), Arg.Any<DateTimeOffset?>()).Returns("new-access-token");
    }

    private RefreshHandler Handler() => new(_sessions, _users, _tenant, _tokens, _clock);

    // A signed-in user with one session that the (fake) database can find by its token.
    private (User user, Session session, string token) GivenSignedIn()
    {
        var user = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        user.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var token = RefreshTokens.Generate();
        var session = Session.Start(user.TenantId, user.Id, RefreshTokens.Hash(token), Now).Value;
        var hash = RefreshTokens.Hash(token);
        _sessions.FindTenantByRefreshHashAsync(hash, Arg.Any<CancellationToken>()).Returns(session.TenantId);
        _sessions.FindByRefreshHashAsync(hash, Arg.Any<CancellationToken>()).Returns(session);
        return (user, session, token);
    }

    [Fact]
    public async Task A_valid_token_gives_new_tokens_and_rotates_the_session()
    {
        var (user, session, token) = GivenSignedIn();

        var r = await Handler().HandleAsync(new RefreshCommand(token));

        r.IsSuccess.Should().BeTrue();
        r.Value.AccessToken.Should().Be("new-access-token");
        r.Value.RefreshToken.Should().NotBe(token);
        session.CurrentRefreshHash.Should().Be(RefreshTokens.Hash(r.Value.RefreshToken));
        _tokens.Received(1).IssueAccessToken(
            Arg.Is<AccessTokenClaims>(c => c.SessionId == session.Id && c.UserId == user.Id), Arg.Any<DateTimeOffset?>());
        await _sessions.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reusing_an_old_token_revokes_the_session_and_saves_that()
    {
        var (_, session, first) = GivenSignedIn();
        (await Handler().HandleAsync(new RefreshCommand(first))).IsSuccess.Should().BeTrue();

        var replay = await Handler().HandleAsync(new RefreshCommand(first));   // a thief replays the old token

        replay.Error.Should().Be(Session.RefreshReused);
        session.RevokedAt.Should().NotBeNull();
        await _sessions.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());   // the revocation was written
    }

    [Fact]
    public async Task An_unknown_token_is_rejected_and_nothing_is_saved()
    {
        var r = await Handler().HandleAsync(new RefreshCommand(RefreshTokens.Generate()));

        r.Error.Should().Be(Session.RefreshInvalid);
        await _sessions.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_token_is_rejected(string? token)
        => (await Handler().HandleAsync(new RefreshCommand(token))).Error.Should().Be(Session.RefreshInvalid);

    [Fact]
    public async Task A_suspended_tenant_ends_the_session()
    {
        var (_, session, token) = GivenSignedIn();
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(false);

        var r = await Handler().HandleAsync(new RefreshCommand(token));

        r.Error.Should().Be(Session.RefreshInvalid);
        session.RevokeReason.Should().Be("user_inactive");
    }
}