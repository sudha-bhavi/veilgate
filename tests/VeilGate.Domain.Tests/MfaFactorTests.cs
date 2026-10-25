using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class MfaFactorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    private static MfaFactor NewFactor() =>
        MfaFactor.EnrollTotp(Tenant, User, "v1:encrypted", Now).Value;

    [Fact]
    public void Enroll_creates_an_unconfirmed_factor()
    {
        var f = NewFactor();

        f.TenantId.Should().Be(Tenant);
        f.UserId.Should().Be(User);
        f.Type.Should().Be(MfaFactorType.Totp);
        f.ProtectedSecret.Should().Be("v1:encrypted");
        f.IsConfirmed.Should().BeFalse();
        f.LastUsedCounter.Should().Be(-1);
    }

    [Fact]
    public void Enroll_rejects_missing_tenant()
        => MfaFactor.EnrollTotp(Guid.Empty, User, "x", Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Enroll_rejects_missing_user()
        => MfaFactor.EnrollTotp(Tenant, Guid.Empty, "x", Now).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enroll_rejects_an_empty_secret(string secret)
        => MfaFactor.EnrollTotp(Tenant, User, secret, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Confirm_marks_the_factor_usable_and_remembers_the_window()
    {
        var f = NewFactor();

        f.Confirm(100, Now).IsSuccess.Should().BeTrue();

        f.IsConfirmed.Should().BeTrue();
        f.ConfirmedAt.Should().Be(Now);
        f.LastUsedCounter.Should().Be(100);
    }

    [Fact]
    public void Confirm_twice_is_a_conflict()
    {
        var f = NewFactor();
        f.Confirm(100, Now);

        var second = f.Confirm(101, Now);

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("mfa.already_confirmed");
    }

    [Fact]
    public void An_unconfirmed_factor_cannot_be_used()
    {
        var result = NewFactor().RecordUse(100, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("mfa.not_confirmed");
    }

    [Fact]
    public void A_newer_window_is_accepted_and_remembered()
    {
        var f = NewFactor();
        f.Confirm(100, Now);

        f.RecordUse(101, Now).IsSuccess.Should().BeTrue();

        f.LastUsedCounter.Should().Be(101);
    }

    [Theory]
    [InlineData(100)]   // same window again = replay
    [InlineData(99)]    // an older window
    public void The_same_or_an_older_window_is_rejected(long counter)
    {
        var f = NewFactor();
        f.Confirm(100, Now);

        var result = f.RecordUse(counter, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("mfa.code_reused");
        f.LastUsedCounter.Should().Be(100);   // unchanged
    }
}