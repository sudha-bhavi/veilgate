using System.Globalization;
using FluentAssertions;
using VeilGate.Application.Policies;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using VeilGate.Domain.Vault;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DateShifterTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void The_offset_is_stable_nonzero_and_within_range()
    {
        var first = DateShifter.OffsetDays(Key, "patient-881", 30);

        first.Should().Be(DateShifter.OffsetDays(Key, "patient-881", 30));
        first.Should().NotBe(0);
        Math.Abs(first).Should().BeLessThanOrEqualTo(30);
    }

    [Fact]
    public void Offsets_are_never_zero_for_many_seeds()
    {
        for (var i = 0; i < 2000; i++)
        {
            var offset = DateShifter.OffsetDays(Key, "seed-" + i, 5);
            offset.Should().NotBe(0);
            Math.Abs(offset).Should().BeLessThanOrEqualTo(5);
        }
    }

    [Theory]
    [InlineData("03/14/2025", 10, "03/24/2025")]
    [InlineData("3/14/2025", 10, "3/24/2025")]
    [InlineData("2025-03-14", -14, "2025-02-28")]
    [InlineData("03/14/25", 1, "03/15/25")]
    public void Shift_keeps_the_format(string input, int days, string expected) =>
        DateShifter.Shift(input, days).Should().Be(expected);

    [Fact]
    public void Text_that_is_not_a_known_date_returns_null() =>
        DateShifter.Shift("sometime in March", 5).Should().BeNull();

    [Fact]
    public void The_gap_between_two_dates_is_preserved()
    {
        var offset = DateShifter.OffsetDays(Key, "s", 365);
        var a = DateTime.ParseExact(DateShifter.Shift("2025-01-01", offset)!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var b = DateTime.ParseExact(DateShifter.Shift("2025-01-31", offset)!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        (b - a).Days.Should().Be(30);
    }

    private static PolicyDecision DateDecision(string text, string value)
    {
        var start = text.IndexOf(value, StringComparison.Ordinal);
        var entity = new DetectedEntity("DATE", start, start + value.Length, 1.0, "test");
        return new PolicyDecision([new DecidedEntity(entity, PolicyAction.Shift, 365)]);
    }

    [Fact]
    public void The_transformer_shifts_a_date_when_the_session_has_a_key()
    {
        var text = "Seen on 03/14/2025.";
        var session = new VaultSession { DateShiftKey = Key };

        var result = new Transformer().Apply(text, DateDecision(text, "03/14/2025"), session);

        result.Text.Should().NotContain("03/14/2025").And.MatchRegex(@"^Seen on \d{2}/\d{2}/\d{4}\.$");
        result.Entities.Single().Action.Should().Be(PolicyAction.Shift);
    }

    [Fact]
    public void Without_a_key_the_transformer_redacts_instead()
    {
        var text = "Seen on 03/14/2025.";

        var result = new Transformer().Apply(text, DateDecision(text, "03/14/2025"), new VaultSession());

        result.Text.Should().Be("Seen on [REDACTED].");
        result.Entities.Single().Action.Should().Be(PolicyAction.Redact);
    }
}