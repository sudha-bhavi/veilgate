using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class SessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    private static string NewHash() => RefreshTokens.Hash(RefreshTokens.Generate());

    private static (Session session, string firstHash) StartSession()
    {
        var hash = NewHash();
        return (Session.Start(Tenant, User, hash, Now).Value, hash);
    }

    [Fact]
    public void Start_creates_an_active_session_with_a_hard_limit()
    {
        var (s, hash) = StartSession();

        s.TenantId.Should().Be(Tenant);
        s.UserId.Should().Be(User);
        s.CurrentRefreshHash.Should().Be(hash);
        s.PreviousRefreshHash.Should().BeNull();
        s.ExpiresAt.Should().Be(Now + Session.AbsoluteTimeout);
        s.IsActive(Now).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void Start_rejects_a_value_that_is_not_a_hash(string bad)
        => Session.Start(Tenant, User, bad, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Start_rejects_a_missing_tenant_or_user()
    {
        Session.Start(Guid.Empty, User, NewHash(), Now).IsFailure.Should().BeTrue();
        Session.Start(Tenant, Guid.Empty, NewHash(), Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Rotate_with_the_current_token_swaps_in_the_new_one()
    {
        var (s, first) = StartSession();
        var second = NewHash();

        s.Rotate(first, second, Now.AddMinutes(10)).IsSuccess.Should().BeTrue();

        s.CurrentRefreshHash.Should().Be(second);
        s.PreviousRefreshHash.Should().Be(first);
        s.LastSeenAt.Should().Be(Now.AddMinutes(10));
        s.IsActive(Now.AddMinutes(10)).Should().BeTrue();
    }

    [Fact]
    public void Rotating_again_with_the_newest_token_keeps_working()
    {
        var (s, first) = StartSession();
        var second = NewHash();
        var third = NewHash();

        s.Rotate(first, second, Now.AddMinutes(10));
        s.Rotate(second, third, Now.AddMinutes(20)).IsSuccess.Should().BeTrue();

        s.CurrentRefreshHash.Should().Be(third);
        s.PreviousRefreshHash.Should().Be(second);
    }

    [Fact]
    public void Presenting_an_old_token_revokes_the_whole_session()
    {
        var (s, first) = StartSession();
        var second = NewHash();
        s.Rotate(first, second, Now.AddMinutes(10));

        var replay = s.Rotate(first, NewHash(), Now.AddMinutes(11));   // the old token comes back

        replay.IsFailure.Should().BeTrue();
        replay.Error.Should().Be(Session.RefreshReused);
        s.RevokedAt.Should().Be(Now.AddMinutes(11));
        s.RevokeReason.Should().Be("refresh_reuse");

        // Even the legitimate newest token is dead now: everyone has to sign in again.
        s.Rotate(second, NewHash(), Now.AddMinutes(12)).Error.Should().Be(Session.RefreshInvalid);
        s.IsActive(Now.AddMinutes(12)).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_token_is_rejected_without_revoking()
    {
        var (s, _) = StartSession();

        var r = s.Rotate(NewHash(), NewHash(), Now.AddMinutes(5));

        r.Error.Should().Be(Session.RefreshInvalid);
        s.RevokedAt.Should().BeNull();
    }

    [Fact]
    public void A_session_idle_for_too_long_is_rejected()
    {
        var (s, first) = StartSession();
        var later = Now + Session.IdleTimeout + TimeSpan.FromSeconds(1);

        s.IsActive(later).Should().BeFalse();
        s.Rotate(first, NewHash(), later).Error.Should().Be(Session.RefreshInvalid);
    }

    [Fact]
    public void Activity_keeps_the_session_alive_until_the_hard_limit()
    {
        var (s, first) = StartSession();
        var second = NewHash();
        var third = NewHash();

        s.Rotate(first, second, Now.AddHours(7)).IsSuccess.Should().BeTrue();    // within 8h idle
        s.Rotate(second, third, Now.AddHours(14)).IsSuccess.Should().BeTrue();   // 7h after the last use, so still fine

        s.IsActive(Now + Session.AbsoluteTimeout + TimeSpan.FromSeconds(1)).Should().BeFalse();   // but never past 7 days
    }

    [Fact]
    public void Rotate_refuses_a_bad_replacement_hash_and_changes_nothing()
    {
        var (s, first) = StartSession();

        s.Rotate(first, "not-a-hash", Now.AddMinutes(1)).IsFailure.Should().BeTrue();

        s.CurrentRefreshHash.Should().Be(first);
    }

    [Fact]
    public void Revoke_records_the_reason_and_keeps_the_first_one()
    {
        var (s, _) = StartSession();

        s.Revoke("logout", Now);
        s.Revoke("something_else", Now.AddMinutes(1));

        s.RevokedAt.Should().Be(Now);
        s.RevokeReason.Should().Be("logout");
        s.IsActive(Now).Should().BeFalse();
    }
}