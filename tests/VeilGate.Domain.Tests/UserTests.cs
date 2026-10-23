using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static User NewUser() => User.Invite(Tenant, "  Jordan@Acme.Test ", " Jordan Reyes ", UserRole.Auditor, Now).Value;

    [Fact]
    public void Invite_normalizes_email_and_name_and_starts_invited()
    {
        var u = NewUser();
        u.Email.Should().Be("jordan@acme.test");
        u.DisplayName.Should().Be("Jordan Reyes");
        u.Status.Should().Be(UserStatus.Invited);
        u.PasswordHash.Should().BeNull();
        u.TenantId.Should().Be(Tenant);
        u.TokenVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("a@b")]
    [InlineData("two@@acme.test")]
    [InlineData("has space@acme.test")]
    public void Invite_rejects_bad_email(string email)
        => User.Invite(Tenant, email, "Name", UserRole.Developer, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Invite_requires_tenant_and_name()
    {
        User.Invite(Guid.Empty, "a@acme.test", "Name", UserRole.Developer, Now).IsFailure.Should().BeTrue();
        User.Invite(Tenant, "a@acme.test", "  ", UserRole.Developer, Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Accepting_invitation_activates_once()
    {
        var u = NewUser();
        u.AcceptInvitation("$argon2id$hash", Now).IsSuccess.Should().BeTrue();
        u.Status.Should().Be(UserStatus.Active);
        u.PasswordChangedAt.Should().Be(Now);
        u.AcceptInvitation("$argon2id$other", Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Five_failed_logins_lock_the_account_for_15_minutes()
    {
        var u = NewUser();
        for (var i = 0; i < User.MaxFailedAttempts - 1; i++) u.RegisterFailedLogin(Now);
        u.IsLocked(Now).Should().BeFalse();
        u.RegisterFailedLogin(Now);
        u.IsLocked(Now).Should().BeTrue();
        u.IsLocked(Now.AddMinutes(14)).Should().BeTrue();
        u.IsLocked(Now.AddMinutes(16)).Should().BeFalse();
        u.FailedAttempts.Should().Be(0);
    }

    [Fact]
    public void Successful_login_clears_failures_and_lock()
    {
        var u = NewUser();
        u.RegisterFailedLogin(Now);
        u.RegisterSuccessfulLogin(Now.AddMinutes(1));
        u.FailedAttempts.Should().Be(0);
        u.LockedUntil.Should().BeNull();
        u.LastLoginAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Disabling_bumps_token_version_so_tokens_die()
    {
        var u = NewUser();
        u.Disable(Now).IsSuccess.Should().BeTrue();
        u.TokenVersion.Should().Be(2);
        u.Disable(Now).IsFailure.Should().BeTrue();
    }
}
