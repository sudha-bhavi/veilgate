using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class InvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static NewInvitation NewOne() =>
        Invitation.Create(Tenant, Guid.NewGuid(), " Jordan@Acme.Test ", UserRole.Developer, Guid.NewGuid(), Now).Value;

    [Fact]
    public void Create_stores_a_hash_of_the_token_never_the_token()
    {
        var made = NewOne();
        made.Token.Should().NotBeNullOrWhiteSpace();
        made.Invitation.TokenHash.Should().Be(RefreshTokens.Hash(made.Token));
        made.Invitation.TokenHash.Should().NotContain(made.Token);
        made.Invitation.Email.Should().Be("jordan@acme.test");
        made.Invitation.TenantId.Should().Be(Tenant);
        made.Invitation.ExpiresAt.Should().Be(Now.AddHours(48));
    }

    [Fact]
    public void Two_invitations_never_share_a_token()
        => NewOne().Token.Should().NotBe(NewOne().Token);

    [Fact]
    public void Create_requires_tenant_user_and_inviter()
    {
        Invitation.Create(Guid.Empty, Guid.NewGuid(), "a@acme.test", UserRole.Developer, Guid.NewGuid(), Now).IsFailure.Should().BeTrue();
        Invitation.Create(Tenant, Guid.Empty, "a@acme.test", UserRole.Developer, Guid.NewGuid(), Now).IsFailure.Should().BeTrue();
        Invitation.Create(Tenant, Guid.NewGuid(), "a@acme.test", UserRole.Developer, Guid.Empty, Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Accept_works_once_inside_48_hours()
    {
        var i = NewOne().Invitation;
        i.Accept(Now.AddHours(47)).IsSuccess.Should().BeTrue();
        i.AcceptedAt.Should().Be(Now.AddHours(47));
        i.Accept(Now.AddHours(47)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Accept_fails_after_expiry_with_the_same_error_as_reuse()
    {
        var late = NewOne().Invitation.Accept(Now.AddHours(49));
        late.IsFailure.Should().BeTrue();

        var i = NewOne().Invitation;
        i.Accept(Now);
        i.Accept(Now).Error.Code.Should().Be(late.Error.Code);
    }

    [Fact]
    public void Revoke_kills_an_unused_link_but_leaves_an_accepted_one_alone()
    {
        var unused = NewOne().Invitation;
        unused.Revoke(Now.AddHours(1));
        unused.IsUsable(Now.AddHours(1)).Should().BeFalse();

        var used = NewOne().Invitation;
        used.Accept(Now);
        used.Revoke(Now.AddHours(1));
        used.AcceptedAt.Should().Be(Now);
    }
}