using System.Security.Cryptography;
using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;
using Xunit;

namespace VeilGate.Domain.Tests;

public class VerifyMfaHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IMfaFactorRepository _factors = Substitute.For<IMfaFactorRepository>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();
    private readonly AesGcmSecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));
        private readonly IRecoveryCodeRepository _recovery = Substitute.For<IRecoveryCodeRepository>();
            private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private AccessTokenClaims? _issued;

    public VerifyMfaHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _tokens.IssueAccessToken(Arg.Do<AccessTokenClaims>(c => _issued = c), Arg.Any<DateTimeOffset?>())
               .Returns("access-token");
    }

      private VerifyMfaHandler Handler() =>
        new(_users, _factors, _recovery, _sessions, _tenant, _protector, _tokens, _clock, new VerifyMfaValidator());

    // An active user with a confirmed MFA factor, as it would sit in the database.
    private (User user, MfaFactor factor, byte[] secret) GivenEnrolledUser(bool confirmed = true)
    {
        var user = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        user.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var secret = Totp.GenerateSecret();
        var factor = MfaFactor.EnrollTotp(user.TenantId, user.Id,
            _protector.Protect(secret, StartTotpEnrollmentHandler.ProtectionContext(user.Id)), Now).Value;
        if (confirmed) factor.Confirm(1, Now);
        _factors.GetTotpForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(factor);
        return (user, factor, secret);
    }

    private static VerifyMfaCommand Cmd(User u, string code, int? version = null) =>
        new(u.Id, u.TenantId, version ?? u.TokenVersion, code);

    private static string CodeNow(byte[] secret) => Totp.Compute(secret, Now.ToUnixTimeSeconds());
    private static string StaleCode(byte[] secret) => Totp.Compute(secret, Now.ToUnixTimeSeconds() - 3600);

    [Fact]
    public async Task A_valid_code_returns_an_access_token_with_the_right_claims()
    {
        var (user, factor, secret) = GivenEnrolledUser();

        var r = await Handler().HandleAsync(Cmd(user, CodeNow(secret)));

        r.IsSuccess.Should().BeTrue();
        r.Value.AccessToken.Should().Be("access-token");
        _issued.Should().NotBeNull();
        _issued!.UserId.Should().Be(user.Id);
        _issued.TenantId.Should().Be(user.TenantId);
        _issued.Role.Should().Be("Developer");
        _issued.Amr.Should().BeEquivalentTo(new[] { "pwd", "otp" });
        _issued.TokenVersion.Should().Be(user.TokenVersion);
        factor.LastUsedCounter.Should().Be(Now.ToUnixTimeSeconds() / 30);
        await _factors.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_counted_and_gets_no_token()
    {
        var (user, _, secret) = GivenEnrolledUser();

        var r = await Handler().HandleAsync(Cmd(user, StaleCode(secret)));

        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(ConfirmTotpHandler.InvalidCode);
        user.FailedAttempts.Should().Be(1);
        _tokens.DidNotReceive().IssueAccessToken(Arg.Any<AccessTokenClaims>(), Arg.Any<DateTimeOffset?>());
    }

    [Fact]
    public async Task The_same_code_cannot_be_used_twice()
    {
        var (user, _, secret) = GivenEnrolledUser();
        var code = CodeNow(secret);

        var first = await Handler().HandleAsync(Cmd(user, code));
        var second = await Handler().HandleAsync(Cmd(user, code));

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(ConfirmTotpHandler.InvalidCode);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_even_for_the_right_code()
    {
        var (user, _, secret) = GivenEnrolledUser();
        for (var i = 0; i < 5; i++)
            await Handler().HandleAsync(Cmd(user, StaleCode(secret)));

        var r = await Handler().HandleAsync(Cmd(user, CodeNow(secret)));

        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(LoginHandler.AccountLocked);
        _issued.Should().BeNull();
    }

    [Fact]
    public async Task A_temporary_token_from_an_older_token_version_is_rejected()
    {
        var (user, _, secret) = GivenEnrolledUser();

        var r = await Handler().HandleAsync(Cmd(user, CodeNow(secret), version: user.TokenVersion + 1));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("auth.token_invalid");
    }

    [Fact]
    public async Task A_user_without_a_confirmed_factor_cannot_verify()
    {
        var (user, _, secret) = GivenEnrolledUser(confirmed: false);

        var r = await Handler().HandleAsync(Cmd(user, CodeNow(secret)));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("mfa.not_enrolled");
    }

    [Fact]
    public async Task An_unknown_user_is_rejected()
    {
        var r = await Handler().HandleAsync(new VerifyMfaCommand(Guid.NewGuid(), Guid.NewGuid(), 1, "123456"));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("auth.token_invalid");
    }

    [Fact]
    public async Task An_empty_code_is_a_validation_error()
    {
        var r = await Handler().HandleAsync(new VerifyMfaCommand(Guid.NewGuid(), Guid.NewGuid(), 1, ""));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("validation.failed");
    }

        [Fact]
    public async Task A_recovery_code_signs_in_once_and_is_marked_as_the_method()
    {
        var (user, _, _) = GivenEnrolledUser();
        var raw = RecoveryCodes.Generate()[0];
        var stored = RecoveryCode.Create(user.TenantId, user.Id, RecoveryCodes.Hash(raw), Now).Value;
        _recovery.FindByHashAsync(user.Id, stored.CodeHash, Arg.Any<CancellationToken>()).Returns(stored);

        // Typed lowercase and without the dash, the way people actually type it.
        var typed = raw.Replace("-", "").ToLowerInvariant();
        var r = await Handler().HandleAsync(Cmd(user, typed));

        r.IsSuccess.Should().BeTrue();
        stored.IsUsed.Should().BeTrue();
        _issued!.Amr.Should().BeEquivalentTo(new[] { "pwd", "recovery" });
    }

    [Fact]
    public async Task A_recovery_code_that_was_already_used_is_rejected_and_counted()
    {
        var (user, _, _) = GivenEnrolledUser();
        var raw = RecoveryCodes.Generate()[0];
        var stored = RecoveryCode.Create(user.TenantId, user.Id, RecoveryCodes.Hash(raw), Now).Value;
        stored.MarkUsed(Now.AddDays(-1));
        _recovery.FindByHashAsync(user.Id, stored.CodeHash, Arg.Any<CancellationToken>()).Returns(stored);

        var r = await Handler().HandleAsync(Cmd(user, raw));

        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(ConfirmTotpHandler.InvalidCode);
        user.FailedAttempts.Should().Be(1);
        _issued.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_recovery_code_is_rejected_and_counted()
    {
        var (user, _, _) = GivenEnrolledUser();
        _recovery.FindByHashAsync(user.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<RecoveryCode?>(null));

        var r = await Handler().HandleAsync(Cmd(user, "ZZZZZ-ZZZZZ"));

        r.IsFailure.Should().BeTrue();
        user.FailedAttempts.Should().Be(1);
    }
        [Fact]
    public async Task A_valid_code_starts_a_session_and_returns_its_refresh_token()
    {
        var (user, _, secret) = GivenEnrolledUser();
        var added = new List<Session>();
        _sessions.When(x => x.Add(Arg.Any<Session>())).Do(c => added.Add(c.Arg<Session>()));

        var r = await Handler().HandleAsync(Cmd(user, CodeNow(secret)));

        r.IsSuccess.Should().BeTrue();
        added.Should().ContainSingle();
        var s = added[0];
        s.UserId.Should().Be(user.Id);
        s.CurrentRefreshHash.Should().Be(RefreshTokens.Hash(r.Value.RefreshToken));   // we stored the hash, gave out the token
        _issued!.SessionId.Should().Be(s.Id);
        r.Value.RefreshExpiresAt.Should().Be(s.ExpiresAt);
    }
}