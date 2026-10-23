using FluentAssertions;
using VeilGate.Infrastructure.Security;
using Xunit;

namespace VeilGate.Domain.Tests;

public class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_uses_argon2id_and_records_its_parameters()
        => _hasher.Hash("a long enough password").Should().StartWith("$argon2id$v=19$m=19456,t=2,p=1$");

    [Fact]
    public void Correct_password_verifies_and_wrong_one_does_not()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        _hasher.Verify("correct horse battery staple", hash).Should().BeTrue();
        _hasher.Verify("Correct horse battery staple", hash).Should().BeFalse();
        _hasher.Verify("", hash).Should().BeFalse();
    }

    [Fact]
    public void Same_password_gets_a_different_salt_every_time()
        => _hasher.Hash("same password here").Should().NotBe(_hasher.Hash("same password here"));

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("$argon2i$v=19$m=19456,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=19456,t=2$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$!!!notbase64!!!$!!!notbase64!!!")]
    public void Malformed_hashes_fail_closed_without_throwing(string encoded)
        => _hasher.Verify("anything", encoded).Should().BeFalse();

    [Fact]
    public void Absurd_memory_in_a_tampered_hash_is_refused_instead_of_allocated()
        => _hasher.Verify("x", "$argon2id$v=19$m=4000000,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").Should().BeFalse();

    [Fact]
    public void Fresh_hash_does_not_need_rehash_but_weaker_ones_do()
    {
        _hasher.NeedsRehash(_hasher.Hash("whatever password")).Should().BeFalse();
        _hasher.NeedsRehash("$argon2id$v=19$m=4096,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").Should().BeTrue();
    }

    [Fact]
    public void Dummy_verify_runs_without_throwing() => _hasher.Invoking(h => h.VerifyDummy("anything")).Should().NotThrow();
}
