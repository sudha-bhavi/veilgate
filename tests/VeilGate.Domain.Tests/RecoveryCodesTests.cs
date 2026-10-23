using System.Text.RegularExpressions;
using FluentAssertions;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RecoveryCodesTests
{
    [Fact]
    public void Generates_ten_different_codes()
    {
        var codes = RecoveryCodes.Generate();
        codes.Should().HaveCount(10);
        codes.Distinct().Should().HaveCount(10);
    }

    [Fact]
    public void Codes_look_like_XXXXX_XXXXX_and_skip_confusing_characters()
    {
        // No I, O, 0 or 1, because people misread them from a printed sheet.
        foreach (var code in RecoveryCodes.Generate())
            Regex.IsMatch(code, "^[A-HJ-NP-Z2-9]{5}-[A-HJ-NP-Z2-9]{5}$").Should().BeTrue(code);
    }

    [Fact]
    public void Two_runs_never_give_the_same_set()
        => RecoveryCodes.Generate().Should().NotBeEquivalentTo(RecoveryCodes.Generate());

    [Theory]
    [InlineData("k7m2p-9qx4h", "K7M2P9QX4H")]
    [InlineData("  K7M2P 9QX4H ", "K7M2P9QX4H")]
    [InlineData("K7M2P9QX4H", "K7M2P9QX4H")]
    public void Normalize_ignores_case_dashes_and_spaces(string typed, string expected)
        => RecoveryCodes.Normalize(typed).Should().Be(expected);

    [Fact]
    public void Hash_is_64_lowercase_hex_characters_and_repeatable()
    {
        var h = RecoveryCodes.Hash("K7M2P-9QX4H");
        h.Should().MatchRegex("^[0-9a-f]{64}$");
        RecoveryCodes.Hash("K7M2P-9QX4H").Should().Be(h);
    }

    [Fact]
    public void Same_code_typed_differently_gives_the_same_hash()
        => RecoveryCodes.Hash("k7m2p-9qx4h").Should().Be(RecoveryCodes.Hash("K7M2P9QX4H"));

    [Fact]
    public void Different_codes_give_different_hashes()
        => RecoveryCodes.Hash("K7M2P-9QX4H").Should().NotBe(RecoveryCodes.Hash("K7M2P-9QX4J"));

    [Fact]
    public void Matches_accepts_the_right_code_in_any_format()
    {
        var stored = RecoveryCodes.Hash("K7M2P-9QX4H");
        RecoveryCodes.Matches("k7m2p 9qx4h", stored).Should().BeTrue();
    }

    [Theory]
    [InlineData("WRONG-CODE1")]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_rejects_wrong_or_empty_input(string typed)
        => RecoveryCodes.Matches(typed, RecoveryCodes.Hash("K7M2P-9QX4H")).Should().BeFalse();

    [Fact]
    public void Matches_does_not_throw_on_a_garbage_stored_hash()
        => RecoveryCodes.Matches("K7M2P-9QX4H", "not-a-hash").Should().BeFalse();
}