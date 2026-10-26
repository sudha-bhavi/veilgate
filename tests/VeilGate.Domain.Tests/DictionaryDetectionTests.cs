using FluentAssertions;
using VeilGate.Application.Detection;
using VeilGate.Domain.Detection;
using VeilGate.Infrastructure.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DictionaryDetectionTests
{
    [Fact]
    public void Finds_every_word_in_one_pass_ignoring_case()
    {
        var ac = new AhoCorasick(["Mercy General", "Dr. Rao"]);
        var text = "Seen at MERCY GENERAL by dr. rao.";

        var hits = ac.Find(text).Select(h => text[h.Start..h.End]).ToList();

        hits.Should().BeEquivalentTo("MERCY GENERAL", "dr. rao");
    }

    [Fact]
    public void Matches_whole_words_only()
    {
        var ac = new AhoCorasick(["Ann"]);

        ac.Find("Annual review for Ann").Should().ContainSingle();
    }

    [Fact]
    public void Reports_overlapping_words_that_end_together()
    {
        var ac = new AhoCorasick(["she", "he"]);

        // "he" is inside "she", but both are whole words only when surrounded by non-letters, so neither matches "ushers".
        ac.Find("ushers").Should().BeEmpty();
    }

    [Fact]
    public void The_detector_uses_the_entity_type_of_its_dictionary()
    {
        var context = new DetectionContext
        {
            Dictionaries = [new DetectionDictionary("clinics", "CUSTOM:CLINIC", new AhoCorasick(["Mercy General"]))],
        };

        var found = new DictionaryDetector().Detect("Seen at Mercy General", context).ToList();

        found.Should().ContainSingle().Which.Type.Should().Be("CUSTOM:CLINIC");
    }
}