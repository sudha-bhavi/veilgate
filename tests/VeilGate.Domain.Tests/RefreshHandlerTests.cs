using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ISecurityEventWriter _events = Substitute.For<ISecurityEventWriter>();
    private readonly TenantContext _tenant = new();

    public RefreshHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _tokens.IssueAccessToken(Arg.Any<AccessTokenClaims>(), Arg.Any<DateTimeOffset?>()).Returns("new-access-token");
        // A mocking library ignores the interface's default method, so the atomic swap is emulated the way the database does it.
        _sessions.TryRotateAsync(Arg.Any<Session>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<Session>().Rotate(call.ArgAt<string>(1), call.ArgAt<string>(2), call.Arg<DateTimeOffset>()).IsSuccess));
    }

    private RefreshHandler Handler() => new(_sessions, _users, _tenant, _tokens, _clock, _events, NullLogger<RefreshHandler>.Instance);

    // A signed-in user with one session that the (fake) database can find by its token.
    private (User user, Session session, string token) GivenSignedIn(bool demo = false)
    {
        var user = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        user.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var token = RefreshTokens.Generate();
        var session = Session.Start(user.TenantId, user.Id, RefreshTokens.Hash(token), Now, demo).Value;
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

    [Fact]
    public async Task Refreshing_a_demo_session_keeps_it_a_demo_session()
    {
        var (_, _, token) = GivenSignedIn(demo: true);

        await Handler().HandleAsync(new RefreshCommand(token));

        _tokens.Received(1).IssueAccessToken(
            Arg.Is<AccessTokenClaims>(c => c.Amr.SequenceEqual(new[] { "demo" })), Arg.Any<DateTimeOffset?>());
    }

    [Fact]
    public async Task Refreshing_a_normal_session_still_says_password_and_second_factor()
    {
        var (_, _, token) = GivenSignedIn();

        await Handler().HandleAsync(new RefreshCommand(token));

        _tokens.Received(1).IssueAccessToken(
            Arg.Is<AccessTokenClaims>(c => c.Amr.SequenceEqual(new[] { "pwd", "mfa" })), Arg.Any<DateTimeOffset?>());
    }

    [Fact]
    public async Task Reusing_an_old_token_raises_a_high_refresh_reuse_security_event()
    {
        var (user, session, first) = GivenSignedIn();
        (await Handler().HandleAsync(new RefreshCommand(first, "203.0.113.9"))).IsSuccess.Should().BeTrue();
        await _events.DidNotReceive().WriteAsync(Arg.Any<SecurityEvent>(), Arg.Any<CancellationToken>());

        await Handler().HandleAsync(new RefreshCommand(first, "203.0.113.9"));

        await _events.Received(1).WriteAsync(
            Arg.Is<SecurityEvent>(e => e.Type == "refresh.reuse" && e.Severity == Severity.High && e.UserId == user.Id && e.TenantId == session.TenantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_request_that_loses_the_atomic_swap_is_a_replay_and_ends_the_session()
    {
        var (_, session, token) = GivenSignedIn();
        // Another request swapped the token between this one's read and its swap.
        _sessions.TryRotateAsync(session, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var r = await Handler().HandleAsync(new RefreshCommand(token));

        r.Error.Should().Be(Session.RefreshReused);
        session.RevokedAt.Should().NotBeNull();
        await _events.Received(1).WriteAsync(Arg.Is<SecurityEvent>(e => e.Type == "refresh.reuse"), Arg.Any<CancellationToken>());
        _tokens.DidNotReceive().IssueAccessToken(Arg.Any<AccessTokenClaims>(), Arg.Any<DateTimeOffset?>());
    }
}
