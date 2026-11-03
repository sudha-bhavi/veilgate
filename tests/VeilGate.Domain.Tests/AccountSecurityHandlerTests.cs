using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using Xunit;

namespace VeilGate.Domain.Tests;

public class AccountSecurityHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Current = "my current passphrase";
    private const string Fresh = "a brand new passphrase";

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IMfaFactorRepository _factors = Substitute.For<IMfaFactorRepository>();
    private readonly IRecoveryCodeRepository _codes = Substitute.For<IRecoveryCodeRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ISecurityEventWriter _events = Substitute.For<ISecurityEventWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public AccountSecurityHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Verify(Current, "stored-hash").Returns(true);
        _hasher.Hash(Fresh).Returns("new-hash");
    }

    private AuthAuditor Auditor() => new(_audit, _events, _clock, NullLogger<AuthAuditor>.Instance);

    private User GivenUser()
    {
        var user = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        user.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    private static Session NewSession(User u) =>
        Session.Start(u.TenantId, u.Id, RefreshTokens.Hash(RefreshTokens.Generate()), Now).Value;

    // ---------------------------------------------------------------- change password

    private ChangePasswordHandler Changer() => new(_users, _sessions, _hasher, _events, _clock, Auditor(), NullLogger<ChangePasswordHandler>.Instance);

    [Fact]
    public async Task A_correct_current_password_sets_the_new_one_and_ends_every_other_session()
    {
        var user = GivenUser();
        var mine = NewSession(user);
        var other = NewSession(user);
        _sessions.ListActiveForUserAsync(user.Id, Now, Arg.Any<CancellationToken>()).Returns(new List<Session> { mine, other });
        var version = user.TokenVersion;

        var r = await Changer().HandleAsync(new ChangePasswordCommand(user.Id, mine.Id, Current, Fresh, "203.0.113.5"));

        r.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().Be("new-hash");
        user.TokenVersion.Should().Be(version + 1);
        mine.RevokedAt.Should().BeNull();
        other.RevokeReason.Should().Be("password_change");
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == "auth.password_change" && e.Result == "success" && e.Details["count"] == "1"),
            Arg.Any<CancellationToken>());
        await _events.Received(1).WriteAsync(
            Arg.Is<SecurityEvent>(e => e.Type == "auth.password_changed" && e.UserId == user.Id && e.Severity == Severity.Info),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_counted_and_changes_nothing()
    {
        var user = GivenUser();

        var r = await Changer().HandleAsync(new ChangePasswordCommand(user.Id, Guid.NewGuid(), "not my password", Fresh));

        r.Error.Should().Be(ChangePasswordHandler.CurrentIncorrect);
        user.FailedAttempts.Should().Be(1);
        user.PasswordHash.Should().Be("stored-hash");
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEvent>(e => e.Action == "auth.password_change" && e.Result == "failure"), Arg.Any<CancellationToken>());
        await _events.DidNotReceive().WriteAsync(Arg.Any<SecurityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Five_wrong_current_passwords_lock_the_account()
    {
        var user = GivenUser();

        for (var i = 0; i < User.MaxFailedAttempts; i++)
            await Changer().HandleAsync(new ChangePasswordCommand(user.Id, Guid.NewGuid(), "wrong", Fresh));
        var next = await Changer().HandleAsync(new ChangePasswordCommand(user.Id, Guid.NewGuid(), Current, Fresh));

        user.IsLocked(Now).Should().BeTrue();
        next.Error.Should().Be(LoginHandler.AccountLocked);
        user.PasswordHash.Should().Be("stored-hash");
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_and_changes_nothing()
    {
        var user = GivenUser();

        var r = await Changer().HandleAsync(new ChangePasswordCommand(user.Id, Guid.NewGuid(), Current, "short"));

        r.Error.Code.Should().Be("password.weak");
        user.PasswordHash.Should().Be("stored-hash");
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_password_built_from_the_email_name_is_refused()
    {
        var user = GivenUser();

        var r = await Changer().HandleAsync(new ChangePasswordCommand(user.Id, Guid.NewGuid(), Current, "jordan is my name 123"));

        r.Error.Code.Should().Be("password.weak");
    }

    [Fact]
    public async Task The_same_password_again_is_refused()
    {
        var user = GivenUser();
        _hasher.Verify(Current, "stored-hash").Returns(true);

        var r = await Changer().HandleAsync(new ChangePasswordCommand(user.Id, Guid.NewGuid(), Current, Current));

        r.Error.Should().Be(ChangePasswordHandler.SameAsCurrent);
        user.PasswordHash.Should().Be("stored-hash");
    }

    [Theory]
    [InlineData(null, Fresh)]
    [InlineData("", Fresh)]
    [InlineData(Current, null)]
    [InlineData(Current, "")]
    public async Task Missing_passwords_are_refused_before_anything_is_looked_up(string? current, string? fresh)
    {
        var r = await Changer().HandleAsync(new ChangePasswordCommand(Guid.NewGuid(), Guid.NewGuid(), current, fresh));

        r.IsFailure.Should().BeTrue();
        await _users.DidNotReceiveWithAnyArgs().GetCurrentTenantUserAsync(default, default);
    }

    [Fact]
    public async Task A_person_who_is_not_active_is_told_to_sign_in_again()
    {
        var invited = User.Invite(Guid.NewGuid(), "new@acme.test", "New", UserRole.Auditor, Now).Value;
        _users.GetCurrentTenantUserAsync(invited.Id, Arg.Any<CancellationToken>()).Returns(invited);

        var r = await Changer().HandleAsync(new ChangePasswordCommand(invited.Id, Guid.NewGuid(), Current, Fresh));

        r.Error.Kind.Should().Be(ErrorKind.Unauthorized);
    }

    // ---------------------------------------------------------------- mfa status

    private MfaFactor GivenFactor(User user, bool confirmed = true)
    {
        var factor = MfaFactor.EnrollTotp(user.TenantId, user.Id, "protected", Now).Value;
        if (confirmed) factor.Confirm(1, Now);
        _factors.GetTotpForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(factor);
        return factor;
    }

    private RecoveryCode Code(User user, bool used = false)
    {
        var code = RecoveryCode.Create(user.TenantId, user.Id, RecoveryCodes.Hash(RecoveryCodes.Generate()[0]), Now).Value;
        if (used) code.MarkUsed(Now);
        return code;
    }

    [Fact]
    public async Task The_status_counts_the_recovery_codes_left()
    {
        var user = GivenUser();
        GivenFactor(user);
        _codes.ListForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(new List<RecoveryCode> { Code(user), Code(user), Code(user, used: true) });

        var status = await new GetMfaStatusHandler(_factors, _codes).HandleAsync(user.Id);

        status.AddedAt.Should().Be(Now);
        status.RecoveryCodesLeft.Should().Be(2);
        status.RecoveryCodesTotal.Should().Be(3);
    }

    [Fact]
    public async Task Without_a_confirmed_authenticator_the_status_is_empty()
    {
        var user = GivenUser();
        GivenFactor(user, confirmed: false);

        var status = await new GetMfaStatusHandler(_factors, _codes).HandleAsync(user.Id);

        status.Should().Be(new MfaStatus(null, 0, 0));
    }

    // ---------------------------------------------------------------- regenerate recovery codes

    private RegenerateRecoveryCodesHandler Regenerator() =>
        new(_users, _factors, _codes, _events, _clock, Auditor(), NullLogger<RegenerateRecoveryCodesHandler>.Instance);

    [Fact]
    public async Task Regenerating_replaces_every_old_code_with_ten_new_ones_that_are_stored_only_as_hashes()
    {
        var user = GivenUser();
        GivenFactor(user);
        var old = new List<RecoveryCode> { Code(user), Code(user, used: true) };
        _codes.ListForUserAsync(user.Id, Arg.Any<CancellationToken>()).Returns(old);
        IEnumerable<RecoveryCode>? stored = null;
        _codes.When(c => c.AddRange(Arg.Any<IEnumerable<RecoveryCode>>())).Do(call => stored = call.Arg<IEnumerable<RecoveryCode>>().ToList());

        var r = await Regenerator().HandleAsync(new RegenerateRecoveryCodesCommand(user.Id, "203.0.113.5"));

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().HaveCount(RecoveryCodes.Count);
        _codes.Received(1).RemoveRange(old);
        stored!.Select(c => c.CodeHash).Should().BeEquivalentTo(r.Value.Select(RecoveryCodes.Hash));
        stored.Should().OnlyContain(c => !c.IsUsed);
        await _codes.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEvent>(e => e.Action == "auth.recovery_codes_regenerate"), Arg.Any<CancellationToken>());
        await _events.Received(1).WriteAsync(
            Arg.Is<SecurityEvent>(e => e.Type == "mfa.recovery_codes_regenerated" && e.UserId == user.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Regenerating_needs_a_confirmed_authenticator()
    {
        var user = GivenUser();
        GivenFactor(user, confirmed: false);

        var r = await Regenerator().HandleAsync(new RegenerateRecoveryCodesCommand(user.Id));

        r.Error.Code.Should().Be("mfa.not_enrolled");
        _codes.DidNotReceiveWithAnyArgs().RemoveRange(default!);
    }
}
