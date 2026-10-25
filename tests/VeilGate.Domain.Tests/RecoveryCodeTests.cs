using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RecoveryCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    // A real hash, made the same way production makes it.
    private static readonly string GoodHash = RecoveryCodes.Hash("K7M2P-9QX4H");

    [Fact]
    public void Create_builds_an_unused_code()
    {
        var c = RecoveryCode.Create(Tenant, User, GoodHash, Now).Value;

        c.TenantId.Should().Be(Tenant);
        c.UserId.Should().Be(User);
        c.CodeHash.Should().Be(GoodHash);
        c.IsUsed.Should().BeFalse();
        c.UsedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("K7M2P-9QX4H")]   // the raw code by mistake
    public void Create_rejects_a_value_that_is_not_a_hash(string bad)
        => RecoveryCode.Create(Tenant, User, bad, Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_rejects_uppercase_hex()
        => RecoveryCode.Create(Tenant, User, GoodHash.ToUpperInvariant(), Now).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_rejects_a_missing_tenant_or_user()
    {
        RecoveryCode.Create(Guid.Empty, User, GoodHash, Now).IsFailure.Should().BeTrue();
        RecoveryCode.Create(Tenant, Guid.Empty, GoodHash, Now).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void MarkUsed_stamps_the_time()
    {
        var c = RecoveryCode.Create(Tenant, User, GoodHash, Now).Value;

        c.MarkUsed(Now).IsSuccess.Should().BeTrue();

        c.IsUsed.Should().BeTrue();
        c.UsedAt.Should().Be(Now);
    }

    [Fact]
    public void A_code_can_only_be_used_once()
    {
        var c = RecoveryCode.Create(Tenant, User, GoodHash, Now).Value;
        c.MarkUsed(Now);

        var second = c.MarkUsed(Now.AddMinutes(5));

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("mfa.recovery_code_used");
        c.UsedAt.Should().Be(Now);   // unchanged
    }
}