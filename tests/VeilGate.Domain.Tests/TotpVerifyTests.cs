using System.Text;
using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class TotpVerifyTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");
    private const long Now = 1_000_000_000;
    private const long NowWindow = Now / 30;

    private static string CodeAt(long t) => Totp.Compute(Secret, t);

    [Fact]
    public void Accepts_the_current_code()
        => Totp.Verify(Secret, CodeAt(Now), Now).Should().Be(NowWindow);

    [Fact]
    public void Accepts_the_previous_window_for_clock_drift()
        => Totp.Verify(Secret, CodeAt(Now - 30), Now).Should().Be(NowWindow - 1);

    [Fact]
    public void Accepts_the_next_window_for_clock_drift()
        => Totp.Verify(Secret, CodeAt(Now + 30), Now).Should().Be(NowWindow + 1);

    [Fact]
    public void Rejects_a_code_from_two_windows_ago()
        => Totp.Verify(Secret, CodeAt(Now - 60), Now).Should().BeNull();

    [Fact]
    public void Rejects_a_code_that_was_already_used()
    {
        var code = CodeAt(Now);
        var first = Totp.Verify(Secret, code, Now);

        first.Should().NotBeNull();
        Totp.Verify(Secret, code, Now, lastUsedCounter: first!.Value).Should().BeNull();
    }

    [Fact]
    public void Accepts_a_code_typed_with_a_space()
    {
        var code = CodeAt(Now);
        Totp.Verify(Secret, code[..3] + " " + code[3..], Now).Should().Be(NowWindow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Rejects_malformed_input(string code)
        => Totp.Verify(Secret, code, Now).Should().BeNull();

    [Fact]
    public void Uri_contains_issuer_account_and_a_secret_that_decodes_back()
    {
        var uri = Totp.BuildUri("VeilGate", "sudha@example.com", Secret);

        uri.Should().StartWith("otpauth://totp/VeilGate:sudha%40example.com?");
        uri.Should().Contain("issuer=VeilGate");
        uri.Should().Contain($"secret={Base32.Encode(Secret)}");
    }

    [Fact]
    public void Generated_secrets_are_20_bytes_and_different_each_time()
    {
        var a = Totp.GenerateSecret();
        var b = Totp.GenerateSecret();

        a.Should().HaveCount(20);
        a.Should().NotEqual(b);
    }
}