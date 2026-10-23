using System.Text;
using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class TotpTests
{
    // The official test secret from RFC 6238: the ASCII text "12345678901234567890".
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_the_official_RFC_6238_test_values(long time, string expected)
    {
        Totp.Compute(Secret, time).Should().Be(expected);
    }

    [Fact]
    public void Same_30_second_window_gives_the_same_code()
    {
        Totp.Compute(Secret, 60).Should().Be(Totp.Compute(Secret, 89));
    }

    [Fact]
    public void Next_window_gives_a_different_code()
    {
        Totp.Compute(Secret, 89).Should().NotBe(Totp.Compute(Secret, 90));
    }
}