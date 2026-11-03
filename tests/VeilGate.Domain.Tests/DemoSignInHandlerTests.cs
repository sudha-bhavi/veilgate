using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DemoSignInHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Email = "demo@acme.test";

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();

    public DemoSignInHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _users.FindLoginCandidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<LoginCandidate?>(null));
        _tokens.IssueAccessToken(Arg.Any<AccessTokenClaims>(), Arg.Any<DateTimeOffset?>()).Returns("demo-access-token");
    }

    private DemoSignInHandler Handler() => new(_users, _sessions, _tenant, _tokens, _clock,
        new AuthAuditor(_audit, _securityEvents, _clock, NullLogger<AuthAuditor>.Instance));

    private User GivenDemoUser(UserRole role = UserRole.SecurityOfficer, bool active = true)
    {
        var user = User.Invite(Guid.NewGuid(), Email, "Demo Visitor", role, Now).Value;
        if (active) user.AcceptInvitation("unusable-hash", Now);
        _users.FindLoginCandidateAsync(Email, Arg.Any<CancellationToken>()).Returns(new LoginCandidate(user.Id, user.TenantId));
        _users.GetCurrentTenantUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    [Fact]
    public async Task It_signs_the_demo_user_in_with_a_demo_session_and_a_demo_token()
    {
        var user = GivenDemoUser();

        var r = await Handler().HandleAsync(new DemoSignInCommand(Email, "203.0.113.5"));

        r.IsSuccess.Should().BeTrue();
        r.Value.AccessToken.Should().Be("demo-access-token");
        r.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        _sessions.Received(1).Add(Arg.Is<Session>(s => s.IsDemo && s.UserId == user.Id && s.ExpiresAt == Now + Session.DemoTimeout));
        await _sessions.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _tokens.Received(1).IssueAccessToken(
            Arg.Is<AccessTokenClaims>(c => c.UserId == user.Id && c.Amr.SequenceEqual(new[] { "demo" }) && c.Role == "SecurityOfficer"),
            Arg.Any<DateTimeOffset?>());
        _tenant.TenantId.Should().Be(user.TenantId);
    }

    [Fact]
    public async Task The_session_keeps_the_address_and_browser()
    {
        GivenDemoUser();

        await Handler().HandleAsync(new DemoSignInCommand(Email, "203.0.113.5", "Mozilla/5.0 Safari/605"));

        _sessions.Received(1).Add(Arg.Is<Session>(s => s.Ip == "203.0.113.5" && s.UserAgent == "Mozilla/5.0 Safari/605"));
    }

    [Fact]
    public async Task It_is_audited_with_the_method_demo()
    {
        var user = GivenDemoUser();

        await Handler().HandleAsync(new DemoSignInCommand(Email, "203.0.113.5"));

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == "auth.demo_login" && e.Result == "success" && e.ActorId == user.Id
                && e.Ip == "203.0.113.5" && e.Details["method"] == "demo"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_configured_email_is_matched_whatever_its_case_or_spacing()
    {
        GivenDemoUser();

        var r = await Handler().HandleAsync(new DemoSignInCommand("  Demo@Acme.Test "));

        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_demo_user_is_simply_unavailable()
    {
        var r = await Handler().HandleAsync(new DemoSignInCommand("nobody@acme.test"));

        r.Error.Should().Be(DemoSignInHandler.Unavailable);
        _sessions.DidNotReceive().Add(Arg.Any<Session>());
    }

    [Theory]
    [InlineData(UserRole.TenantAdmin)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task A_demo_user_who_could_manage_people_or_the_platform_is_refused(UserRole role)
    {
        GivenDemoUser(role);

        var r = await Handler().HandleAsync(new DemoSignInCommand(Email));

        r.Error.Should().Be(DemoSignInHandler.Unavailable);
        _sessions.DidNotReceive().Add(Arg.Any<Session>());
    }

    [Fact]
    public async Task A_demo_user_who_is_not_active_is_refused()
    {
        GivenDemoUser(active: false);

        var r = await Handler().HandleAsync(new DemoSignInCommand(Email));

        r.Error.Should().Be(DemoSignInHandler.Unavailable);
    }

    [Fact]
    public async Task A_suspended_tenant_is_refused()
    {
        GivenDemoUser();
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(false);

        var r = await Handler().HandleAsync(new DemoSignInCommand(Email));

        r.Error.Should().Be(DemoSignInHandler.Unavailable);
    }
}
