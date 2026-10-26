using FluentAssertions;
using VeilGate.Domain.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DetectionBasicsTests
{
    [Theory]
    [InlineData("4111 1111 1111 1111", true)]   // well-known test Visa number
    [InlineData("4111 1111 1111 1112", false)]  // one digit off
    public void Credit_card_uses_luhn(string value, bool expected) =>
        Checksums.CreditCard(value).Should().Be(expected);

    [Theory]
    [InlineData("123-45-6789", true)]
    [InlineData("000-12-3456", false)]
    [InlineData("666-12-3456", false)]
    [InlineData("123-00-6789", false)]
    public void Ssn_follows_ssa_rules(string value, bool expected) =>
        Checksums.Ssn(value).Should().Be(expected);

    [Fact]
    public void Overlapping_detections_keep_the_higher_priority()
    {
        var weak = new DetectedEntity("ZIP", 0, 5, 0.6, "regex", Priority: 50);
        var strong = new DetectedEntity("PHONE", 0, 12, 1.0, "regex", Priority: 100);

        var merged = SpanMerger.Merge([weak, strong]);

        merged.Should().ContainSingle().Which.Type.Should().Be("PHONE");
    }

    [Fact]
    public void Normalizer_removes_zero_width_characters()
    {
        TextNormalizer.Normalize("5\u200B5\u200B5").Should().Be("555");
    }
}