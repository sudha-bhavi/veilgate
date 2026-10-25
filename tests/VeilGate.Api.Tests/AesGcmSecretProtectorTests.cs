using System.Security.Cryptography;
using FluentAssertions;
using VeilGate.Infrastructure.Security;
using Xunit;

namespace VeilGate.Api.Tests;

public class AesGcmSecretProtectorTests
{
    private static AesGcmSecretProtector NewProtector() =>
        new(RandomNumberGenerator.GetBytes(32));

    private static readonly byte[] Secret = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    [Fact]
    public void Round_trip_returns_the_original_bytes()
    {
        var p = NewProtector();
        var stored = p.Protect(Secret, "user-1");

        p.Unprotect(stored, "user-1").Should().Equal(Secret);
    }

    [Fact]
    public void Same_input_gives_different_output_each_time()
    {
        var p = NewProtector();

        p.Protect(Secret, "user-1").Should().NotBe(p.Protect(Secret, "user-1"));
    }

    [Fact]
    public void Stored_value_does_not_contain_the_plain_bytes()
    {
        var stored = NewProtector().Protect(Secret, "user-1");

        stored.Should().StartWith("v1:");

        var blob = Convert.FromBase64String(stored[3..]);
        blob.AsSpan().IndexOf(Secret.AsSpan()).Should().Be(-1);   // the whole sequence must not appear
    }

    [Fact]
    public void Wrong_context_fails()
    {
        var p = NewProtector();
        var stored = p.Protect(Secret, "user-1");

        var act = () => p.Unprotect(stored, "user-2");
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Wrong_key_fails()
    {
        var stored = NewProtector().Protect(Secret, "user-1");

        var act = () => NewProtector().Unprotect(stored, "user-1");
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Tampered_data_fails()
    {
        var p = NewProtector();
        var raw = Convert.FromBase64String(p.Protect(Secret, "user-1")[3..]);
        raw[^1] ^= 1;   // flip a single bit
        var tampered = "v1:" + Convert.ToBase64String(raw);

        var act = () => p.Unprotect(tampered, "user-1");
        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1:not base64!!")]
    [InlineData("v1:AAAA")]
    public void Garbage_input_throws_a_cryptographic_exception(string bad)
    {
        var act = () => NewProtector().Unprotect(bad, "user-1");
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Key_must_be_32_bytes()
    {
        var act = () => new AesGcmSecretProtector(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }
}