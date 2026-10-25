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

public class MfaEnrollmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IMfaFactorRepository _factors = Substitute.For<IMfaFactorRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();
    private readonly AesGcmSecretProtector _protector = new(RandomNumberGenerator.GetBytes(32));
        private readonly IRecoveryCodeRepository _recovery = Substitute.For<IRecoveryCodeRepository>();

    public MfaEnrollmentHandlerTests() => _clock.UtcNow.Returns(Now);

    private StartTotpEnrollmentHandler Start() => new(_users, _factors, _tenant, _protector, _clock);
    private ConfirmTotpHandler Confirm() => new(_users, _factors, _recovery, _tenant, _protector, _clock, new ConfirmTotpValidator());

    private User GivenActiveUser()
    {
        var u = User.Invite(Guid.NewGuid(), "jordan@acme.test", "Jordan", UserRole.Developer, Now).Value;
        u.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(u);
        return u;
    }

    // A factor as it would sit in the database: secret encrypted, optionally already confirmed.
    private (MfaFactor factor, byte[] secret) GivenFactor(User u, bool confirmed = false)
    {
        var secret = Totp.GenerateSecret();
        var factor = MfaFactor.EnrollTotp(u.TenantId, u.Id,
            _protector.Protect(secret, StartTotpEnrollmentHandler.ProtectionContext(u.Id)), Now).Value;
        if (confirmed) factor.Confirm(1, Now);
        _factors.GetTotpForUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(factor);
        return (factor, secret);
    }

    // ---------- Start ----------

    [Fact]
    public async Task Start_saves_an_unconfirmed_encrypted_factor_and_returns_the_qr_link()
    {
        var u = GivenActiveUser();
        MfaFactor? added = null;
        _factors.When(f => f.Add(Arg.Any<MfaFactor>())).Do(c => added = c.Arg<MfaFactor>());

        var r = await Start().HandleAsync(new StartTotpEnrollmentCommand(u.Id, u.TenantId));

        r.IsSuccess.Should().BeTrue();
        r.Value.OtpAuthUri.Should().StartWith("otpauth://totp/VeilGate:jordan%40acme.test?");

        added.Should().NotBeNull();
        added!.IsConfirmed.Should().BeFalse();
        added.ProtectedSecret.Should().StartWith("v1:").And.NotContain(r.Value.ManualEntryKey);

        // The stored secret decrypts to exactly the key we showed the user.
        var decrypted = _protector.Unprotect(added.ProtectedSecret, StartTotpEnrollmentHandler.ProtectionContext(u.Id));
        Base32.Encode(decrypted).Should().Be(r.Value.ManualEntryKey);

        await _factors.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Start_refuses_when_mfa_is_already_confirmed()
    {
        var u = GivenActiveUser();
        GivenFactor(u, confirmed: true);

        var r = await Start().HandleAsync(new StartTotpEnrollmentCommand(u.Id, u.TenantId));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("mfa.already_enrolled");
        _factors.DidNotReceive().Add(Arg.Any<MfaFactor>());
    }

    [Fact]
    public async Task Start_replaces_an_abandoned_unconfirmed_setup()
    {
        var u = GivenActiveUser();
        var (old, _) = GivenFactor(u, confirmed: false);

        var r = await Start().HandleAsync(new StartTotpEnrollmentCommand(u.Id, u.TenantId));

        r.IsSuccess.Should().BeTrue();
        _factors.Received(1).Remove(old);
        _factors.Received(1).Add(Arg.Any<MfaFactor>());
    }

    [Fact]
    public async Task Start_rejects_an_unknown_user()
    {
        var r = await Start().HandleAsync(new StartTotpEnrollmentCommand(Guid.NewGuid(), Guid.NewGuid()));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("auth.invalid_token");
    }

    // ---------- Confirm ----------

    [Fact]
    public async Task Confirm_with_the_right_code_activates_the_factor()
    {
        var u = GivenActiveUser();
        var (factor, secret) = GivenFactor(u);
        var code = Totp.Compute(secret, Now.ToUnixTimeSeconds());

        var r = await Confirm().HandleAsync(new ConfirmTotpCommand(u.Id, u.TenantId, code));

        r.IsSuccess.Should().BeTrue();
        factor.IsConfirmed.Should().BeTrue();
        factor.LastUsedCounter.Should().Be(Now.ToUnixTimeSeconds() / 30);
        await _factors.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_with_a_wrong_code_changes_nothing()
    {
        var u = GivenActiveUser();
        var (factor, secret) = GivenFactor(u);
        var staleCode = Totp.Compute(secret, Now.ToUnixTimeSeconds() - 3600);   // an hour old

        var r = await Confirm().HandleAsync(new ConfirmTotpCommand(u.Id, u.TenantId, staleCode));

        r.IsFailure.Should().BeTrue();
        r.Error.Should().Be(ConfirmTotpHandler.InvalidCode);
        factor.IsConfirmed.Should().BeFalse();
        await _factors.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_without_starting_setup_is_not_found()
    {
        var u = GivenActiveUser();
        _factors.GetTotpForUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MfaFactor?>(null));

        var r = await Confirm().HandleAsync(new ConfirmTotpCommand(u.Id, u.TenantId, "123456"));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("mfa.not_enrolled");
    }

    [Fact]
    public async Task Confirm_twice_is_a_conflict()
    {
        var u = GivenActiveUser();
        GivenFactor(u, confirmed: true);

        var r = await Confirm().HandleAsync(new ConfirmTotpCommand(u.Id, u.TenantId, "123456"));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("mfa.already_confirmed");
    }

    [Fact]
    public async Task Confirm_rejects_an_empty_code_before_touching_anything()
    {
        var r = await Confirm().HandleAsync(new ConfirmTotpCommand(Guid.NewGuid(), Guid.NewGuid(), ""));

        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("validation.failed");
        await _users.DidNotReceive().GetCurrentTenantUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

        [Fact]
    public async Task Confirm_creates_ten_recovery_codes_stores_only_hashes_and_returns_the_readable_ones()
    {
        var u = GivenActiveUser();
        var (_, secret) = GivenFactor(u);
        List<RecoveryCode>? saved = null;
        _recovery.When(x => x.AddRange(Arg.Any<IEnumerable<RecoveryCode>>()))
                 .Do(c => saved = c.Arg<IEnumerable<RecoveryCode>>().ToList());

        var r = await Confirm().HandleAsync(new ConfirmTotpCommand(u.Id, u.TenantId, Totp.Compute(secret, Now.ToUnixTimeSeconds())));

        r.IsSuccess.Should().BeTrue();
        r.Value.RecoveryCodes.Should().HaveCount(10);
        saved.Should().NotBeNull().And.HaveCount(10);
        saved!.Select(s => s.CodeHash).Should().BeEquivalentTo(r.Value.RecoveryCodes.Select(RecoveryCodes.Hash));
        saved.Select(s => s.CodeHash).Should().NotIntersectWith(r.Value.RecoveryCodes);   // hashes, never the readable codes
    }

    [Fact]
    public async Task A_wrong_confirm_code_creates_no_recovery_codes()
    {
        var u = GivenActiveUser();
        var (_, secret) = GivenFactor(u);

        await Confirm().HandleAsync(new ConfirmTotpCommand(u.Id, u.TenantId, Totp.Compute(secret, Now.ToUnixTimeSeconds() - 3600)));

        _recovery.DidNotReceive().AddRange(Arg.Any<IEnumerable<RecoveryCode>>());
    }
}