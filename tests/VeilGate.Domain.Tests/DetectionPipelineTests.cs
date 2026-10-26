using FluentAssertions;
using VeilGate.Application.Detection;
using VeilGate.Infrastructure.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DetectionPipelineTests
{
    private static DetectionPipeline Pipeline() => new([new RegexDetector()]);

    [Fact]
    public async Task Returns_findings_sorted_by_position()
    {
        var text = "Mail a@b.test or call 513-555-0142";
        var found = await Pipeline().DetectAsync(text, DetectionContext.Default);

        found.Select(e => e.Type).Should().Equal("EMAIL", "PHONE");
        found.Should().BeInAscendingOrder(e => e.Start);
    }

    [Fact]
    public async Task Drops_findings_below_the_minimum_confidence()
    {
        var text = "Call 513-555-0142";   // the phone rule has confidence 0.9
        var found = await Pipeline().DetectAsync(text, new DetectionContext { MinConfidence = 0.95 });

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ssn_is_not_also_reported_as_a_phone()
    {
        var found = await Pipeline().DetectAsync("SSN 123-45-6789", DetectionContext.Default);

        found.Should().ContainSingle().Which.Type.Should().Be("SSN");
    }
}