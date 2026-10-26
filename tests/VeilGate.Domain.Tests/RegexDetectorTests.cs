using FluentAssertions;
using VeilGate.Application.Detection;
using VeilGate.Infrastructure.Detection;
using Xunit;

namespace VeilGate.Domain.Tests;

public class RegexDetectorTests
{
    private static List<(string Type, string Value)> Find(string text) =>
        new RegexDetector().Detect(text, DetectionContext.Default)
            .Select(e => (e.Type, text[e.Start..e.End]))
            .ToList();

    [Fact]
    public void Finds_a_phone_number()
    {
        Find("Call John at 513-555-0142 today")
            .Should().Contain(("PHONE", "513-555-0142"));
    }

    [Fact]
    public void Finds_an_email()
    {
        Find("Mail s.bhavi@acme.test please")
            .Should().Contain(("EMAIL", "s.bhavi@acme.test"));
    }

    [Fact]
    public void Reports_only_the_mrn_number_not_the_label()
    {
        Find("Patient MRN 4471882 was seen")
            .Should().Contain(("MRN", "4471882"));
    }

    [Fact]
    public void Ignores_a_credit_card_number_that_fails_luhn()
    {
        Find("Card 4111 1111 1111 1112").Should().NotContain(x => x.Type == "CREDIT_CARD");
    }

    [Fact]
    public void Finds_a_valid_credit_card_number()
    {
        Find("Card 4111 1111 1111 1111").Should().Contain(x => x.Type == "CREDIT_CARD");
    }

    [Fact]
    public void Ignores_an_ssn_with_an_invalid_area()
    {
        Find("ID 000-12-3456").Should().NotContain(x => x.Type == "SSN");
    }
}