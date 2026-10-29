using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Domain.Tests;

public class LoginHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();

        private readonly IMfaFactorRepository _factors = Substitute.For<IMfaFactorRepository>();

    public LoginHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _tokens.IssueMfaChallengeToken(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTimeOffset?>()).Returns("mfa-token");
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();

    private LoginHandler Handler() => new(_users, _factors, _hasher, _tenant, _tokens, _clock,
        new AuthAuditor(_audit, _securityEvents, _clock, NullLogger<AuthAuditor>.Instance), new LoginValidator());

    private Task AuditedAsync(string result, string reason) =>
        _audit.Received(1).WriteAsync(Arg.Is<AuditEvent>(e =>
            e.Action == AuthAuditor.SignIn && e.Result == result && e.Details["reason"] == reason && e.Ip == "203.0.113.5"),
            Arg.Any<CancellationToken>());

    private User GivenActiveUser()
    {
        var u = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        u.AcceptInvitation("stored-hash", Now);
        GivenUserExists(u);
        return u;
    }

    private void GivenUserExists(User u)
    {
        _users.FindLoginCandidateAsync("jordan@acme.test", Arg.Any<CancellationToken>()).Returns(new LoginCandidate(u.Id, u.TenantId));
        _users.GetCurrentTenantUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(u);
    }

    [Fact]
    public async Task Unknown_email_gets_the_generic_error_and_still_burns_hashing_time()
    {
        _users.FindLoginCandidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<LoginCandidate?>(null));

        var r = await Handler().HandleAsync(new LoginCommand("nobody@acme.test", "pw"));

        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(LoginHandler.InvalidCredentials);
        _hasher.Received(1).VerifyDummy("pw");
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Wrong_password_counts_a_failure_and_saves_it()
    {
        var u = GivenActiveUser();
        _hasher.Verify("bad", "stored-hash").Returns(false);

        var r = await Handler().HandleAsync(new LoginCommand("Jordan@Acme.test ", "bad"));

        r.Error.Should().Be(LoginHandler.InvalidCredentials);
        u.FailedAttempts.Should().Be(1);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Wrong_password_is_audited_as_a_failure_with_the_address_and_no_password()
    {
        GivenActiveUser();
        _hasher.Verify("bad", "stored-hash").Returns(false);

        await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "bad", "203.0.113.5"));

        await AuditedAsync("failure", "password");
        await _audit.DidNotReceive().WriteAsync(Arg.Is<AuditEvent>(e => e.Details.Values.Contains("bad")), Arg.Any<CancellationToken>());
        await _securityEvents.DidNotReceive().WriteAsync(Arg.Any<SecurityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_failure_that_locks_the_account_raises_a_security_event()
    {
        var u = GivenActiveUser();
        for (var i = 0; i < User.MaxFailedAttempts - 1; i++) u.RegisterFailedLogin(Now);
        _hasher.Verify("bad", "stored-hash").Returns(false);

        await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "bad", "203.0.113.5"));

        u.IsLocked(Now).Should().BeTrue();
        await _securityEvents.Received(1).WriteAsync(
            Arg.Is<SecurityEvent>(e => e.Type == "auth.account_locked" && e.UserId == u.Id && e.Severity == Severity.Medium),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_locked_account_is_audited_as_denied_without_a_new_lock_event()
    {
        var u = GivenActiveUser();
        for (var i = 0; i < User.MaxFailedAttempts; i++) u.RegisterFailedLogin(Now);

        await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "anything", "203.0.113.5"));

        await AuditedAsync("denied", "locked");
        await _securityEvents.DidNotReceive().WriteAsync(Arg.Any<SecurityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_email_is_not_audited_because_no_tenant_owns_it()
    {
        _users.FindLoginCandidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<LoginCandidate?>(null));

        await Handler().HandleAsync(new LoginCommand("nobody@acme.test", "pw", "203.0.113.5"));

        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_password_step_alone_is_not_a_sign_in()
    {
        GivenActiveUser();
        _hasher.Verify("pw", "stored-hash").Returns(true);

        await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "pw", "203.0.113.5"));

        // Recorded once the second factor passes (VerifyMfaHandler), not here.
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Locked_account_is_refused_without_checking_the_password()
    {
        var u = GivenActiveUser();
        for (var i = 0; i < User.MaxFailedAttempts; i++) u.RegisterFailedLogin(Now);

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "anything"));

        r.Error.Should().Be(LoginHandler.AccountLocked);
        _hasher.DidNotReceive().Verify(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Invited_user_is_treated_like_an_unknown_one()
    {
        var u = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        GivenUserExists(u);

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "pw"));

        r.Error.Should().Be(LoginHandler.InvalidCredentials);
        _hasher.Received(1).VerifyDummy("pw");
    }

    [Fact]
    public async Task Suspended_tenant_is_treated_like_an_unknown_user()
    {
        GivenActiveUser();
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(false);

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "pw"));

        r.Error.Should().Be(LoginHandler.InvalidCredentials);
    }

    [Fact]
    public async Task Correct_password_sets_the_tenant_clears_failures_and_returns_an_mfa_token()
    {
        var u = GivenActiveUser();
        u.RegisterFailedLogin(Now);
        _hasher.Verify("good password", "stored-hash").Returns(true);

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "good password"));

        r.IsSuccess.Should().BeTrue();
        r.Value.MfaToken.Should().Be("mfa-token");
        r.Value.MfaEnrollmentRequired.Should().BeTrue();
        u.FailedAttempts.Should().Be(0);
        _tenant.TenantId.Should().Be(u.TenantId);
    }

    [Fact]
    public async Task Empty_email_is_a_validation_error_and_touches_nothing()
    {
        var r = await Handler().HandleAsync(new LoginCommand("", "pw"));

        r.Error.Kind.Should().Be(ErrorKind.Validation);
        await _users.DidNotReceive().FindLoginCandidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task User_without_mfa_is_told_to_enroll()
    {
        var u = GivenActiveUser();
        _hasher.Verify("pw", "stored-hash").Returns(true);
        _factors.GetTotpForUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MfaFactor?>(null));

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "pw"));

        r.IsSuccess.Should().BeTrue();
        r.Value.MfaRequired.Should().BeFalse();
        r.Value.MfaEnrollmentRequired.Should().BeTrue();
    }

    [Fact]
    public async Task User_with_a_confirmed_factor_is_asked_for_a_code()
    {
        var u = GivenActiveUser();
        _hasher.Verify("pw", "stored-hash").Returns(true);
        var factor = MfaFactor.EnrollTotp(u.TenantId, u.Id, "v1:x", Now).Value;
        factor.Confirm(1, Now);
        _factors.GetTotpForUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(factor);

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "pw"));

        r.IsSuccess.Should().BeTrue();
        r.Value.MfaRequired.Should().BeTrue();
        r.Value.MfaEnrollmentRequired.Should().BeFalse();
    }

    [Fact]
    public async Task An_unconfirmed_factor_still_means_enroll()
    {
        var u = GivenActiveUser();
        _hasher.Verify("pw", "stored-hash").Returns(true);
        var factor = MfaFactor.EnrollTotp(u.TenantId, u.Id, "v1:x", Now).Value;   // never confirmed
        _factors.GetTotpForUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(factor);

        var r = await Handler().HandleAsync(new LoginCommand("jordan@acme.test", "pw"));

        r.Value.MfaRequired.Should().BeFalse();
        r.Value.MfaEnrollmentRequired.Should().BeTrue();
    }
}
