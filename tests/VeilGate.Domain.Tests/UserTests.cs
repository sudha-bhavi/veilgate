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
        [Fact]
    public void Changing_role_bumps_token_version_and_refuses_the_same_role()
    {
        var u = NewUser();
        u.ChangeRole(UserRole.Developer, Now).IsSuccess.Should().BeTrue();
        u.Role.Should().Be(UserRole.Developer);
        u.TokenVersion.Should().Be(2);
        u.ChangeRole(UserRole.Developer, Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void A_tenant_cannot_hand_out_the_platform_admin_role()
    {
        var u = NewUser();
        u.ChangeRole(UserRole.PlatformAdmin, Now).IsFailure.Should().BeTrue();
        u.Role.Should().Be(UserRole.Auditor);
    }

    [Fact]
    public void Enabling_returns_an_activated_user_to_active()
    {
        var u = NewUser();
        u.AcceptInvitation("$argon2id$hash", Now);
        u.Disable(Now);
        u.Enable(Now).IsSuccess.Should().BeTrue();
        u.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public void Enabling_someone_who_never_accepted_returns_them_to_invited()
    {
        var u = NewUser();
        u.Disable(Now);
        u.Enable(Now).IsSuccess.Should().BeTrue();
        u.Status.Should().Be(UserStatus.Invited);
    }

    [Fact]
    public void Enable_only_works_on_a_disabled_user()
        => NewUser().Enable(Now).IsFailure.Should().BeTrue();
    
        [Fact]
    public void Renaming_trims_the_name()
    {
        var u = NewUser();
        u.Rename("  Jordan Q. Reyes ", Now).IsSuccess.Should().BeTrue();
        u.DisplayName.Should().Be("Jordan Q. Reyes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Renaming_rejects_an_empty_name(string name)
        => NewUser().Rename(name, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Renaming_rejects_a_name_over_120_characters()
        => NewUser().Rename(new string('a', 121), Now).IsFailure.Should().BeTrue();
}
