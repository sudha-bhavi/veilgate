using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using VeilGate.Application.Evaluation;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Evaluation;
using Xunit;

namespace VeilGate.Domain.Tests;

public class SyntheticDatasetGeneratorTests
{
    private static readonly IReadOnlyList<GeneratedCase> Sample = SyntheticDatasetGenerator.Generate(1000, seed: 7);

    private static string Slice(GeneratedCase c, LabeledSpan s) => c.Text[s.Start..s.End];

    [Fact]
    public void The_same_seed_gives_exactly_the_same_notes_and_labels()
    {
        var a = SyntheticDatasetGenerator.Generate(50, seed: 42);
        var b = SyntheticDatasetGenerator.Generate(50, seed: 42);

        a.Select(c => c.Text).Should().Equal(b.Select(c => c.Text));
        a.SelectMany(c => c.Expected).Should().Equal(b.SelectMany(c => c.Expected));
    }

    [Fact]
    public void A_different_seed_gives_different_notes()
        => SyntheticDatasetGenerator.Generate(50, seed: 1).Select(c => c.Text)
            .Should().NotEqual(SyntheticDatasetGenerator.Generate(50, seed: 2).Select(c => c.Text));

    [Fact]
    public void It_makes_the_number_of_notes_asked_for()
        => SyntheticDatasetGenerator.Generate(37).Should().HaveCount(37);

    [Fact]
    public void Asking_for_no_notes_is_a_programming_error()
    {
        var act = () => SyntheticDatasetGenerator.Generate(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Every_label_lies_inside_its_note_and_labels_never_overlap()
    {
        foreach (var c in Sample)
        {
            var ordered = c.Expected.OrderBy(s => s.Start).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Start.Should().BeGreaterThanOrEqualTo(0);
                ordered[i].End.Should().BeLessThanOrEqualTo(c.Text.Length);
                ordered[i].End.Should().BeGreaterThan(ordered[i].Start);
                if (i > 0) ordered[i].Start.Should().BeGreaterThanOrEqualTo(ordered[i - 1].End);
            }
        }
    }

    [Fact]
    public void Every_note_is_accepted_by_the_domain()
    {
        var tenant = Guid.NewGuid();
        var dataset = Guid.NewGuid();
        for (var i = 0; i < Sample.Count; i++)
            EvalCase.Create(tenant, dataset, i, Sample[i].Text, Sample[i].Expected).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void The_labelled_text_is_a_valid_value_of_its_type()
    {
        foreach (var c in Sample)
            foreach (var span in c.Expected)
            {
                var value = Slice(c, span);
                switch (span.Type)
                {
                    case EntityTypes.Ssn: Checksums.Ssn(value).Should().BeTrue(value); value.Should().MatchRegex(@"^\d{3}-\d{2}-\d{4}$"); break;
                    case EntityTypes.CreditCard: Checksums.CreditCard(value).Should().BeTrue(value); break;
                    case EntityTypes.Npi: Checksums.Npi(value).Should().BeTrue(value); break;
                    case EntityTypes.Mrn: value.Should().MatchRegex(@"^\d{7}$"); break;
                    case EntityTypes.Email: value.Should().MatchRegex(@"^[a-z.]+@[a-z.]+$"); break;
                    case EntityTypes.Phone: value.Count(char.IsAsciiDigit).Should().Be(10); break;
                    case EntityTypes.Date: value.Should().MatchRegex(@"^(\d{2}/\d{2}/\d{4}|\d{4}-\d{2}-\d{2})$"); break;
                    case EntityTypes.IpAddress: IPAddress.TryParse(value, out _).Should().BeTrue(value); break;
                    case EntityTypes.Url: value.Should().StartWith("https://"); break;
                    case EntityTypes.Person: value.Should().MatchRegex(@"^[A-Z][a-z]+( [A-Z][a-z]+)?$"); break;
                    default: throw new Xunit.Sdk.XunitException($"Unexpected label type {span.Type}");
                }
            }
    }

    [Fact]
    public void A_large_dataset_covers_every_kind_of_value()
        => Sample.SelectMany(c => c.Expected).Select(s => s.Type).Distinct().Should().BeEquivalentTo(
            [EntityTypes.Person, EntityTypes.Ssn, EntityTypes.Mrn, EntityTypes.Phone, EntityTypes.Email, EntityTypes.Date,
             EntityTypes.CreditCard, EntityTypes.Npi, EntityTypes.IpAddress, EntityTypes.Url]);

    [Fact]
    public void About_a_tenth_of_the_notes_contain_nothing_to_find()
        => Sample.Count(c => c.Expected.Count == 0).Should().BeInRange(50, 150);
}