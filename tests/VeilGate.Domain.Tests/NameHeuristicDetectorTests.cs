using FluentAssertions;
using VeilGate.Application.Detection;
using VeilGate.Infrastructure.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class NameHeuristicDetectorTests
{
    private static List<string> Names(string text) =>
        new NameHeuristicDetector().Detect(text, DetectionContext.Default).Select(e => text[e.Start..e.End]).ToList();

    [Theory]
    [InlineData("Patient John Smith was admitted", "John Smith")]
    [InlineData("patient Mary Jones was admitted", "Mary Jones")]
    [InlineData("Seen by Dr. Rao today", "Rao")]
    [InlineData("Seen by Dr Anna Lopez today", "Anna Lopez")]
    [InlineData("Name: Mary O'Neil", "Mary O'Neil")]
    [InlineData("Mother: Anna-Maria Silva", "Anna-Maria Silva")]
    public void Finds_names_from_titles_and_labels(string text, string expected) =>
        Names(text).Should().Contain(expected);

    [Theory]
    [InlineData("Patient was seen and is doing well.")]
    [InlineData("The patient needs follow-up.")]
    [InlineData("Name: Smith")]                       // a single word after a label is too weak
    [InlineData("the doctor, dr. smith, agreed")]    // lowercase names are not recognised
    public void Ignores_text_without_a_name_shape(string text) =>
        Names(text).Should().BeEmpty();

    [Fact]
    public void A_name_stops_at_punctuation()
    {
        Names("Patient John Smith, MRN 4471882, seen").Should().Equal("John Smith");
    }

    [Fact]
    public async Task Works_together_with_the_rule_detectors_and_does_not_swallow_an_mrn()
    {
        var pipeline = new DetectionPipeline([new RegexDetector(), new NameHeuristicDetector()]);
        var text = "Patient John Smith, MRN 4471882";

        var found = await pipeline.DetectAsync(text, DetectionContext.Default);

        found.Select(e => (e.Type, text[e.Start..e.End])).Should().Equal(("PERSON", "John Smith"), ("MRN", "4471882"));
    }
}
