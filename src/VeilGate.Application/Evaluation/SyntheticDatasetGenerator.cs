using System.Text;
using System.Text.RegularExpressions;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Application.Evaluation;

public sealed record GeneratedCase(string Text, IReadOnlyList<LabeledSpan> Expected);

/// <summary>
/// Writes synthetic clinical-style notes and records exactly where each sensitive value sits (Database doc 4.2). Everything is invented:
/// no real names, numbers or records. The same seed always gives the same notes, so two policies can be compared on identical data.
/// </summary>
public static class SyntheticDatasetGenerator
{
    public const int DefaultSeed = 1337;

    // Placeholders in these templates become generated values. The value alone is labelled, not the words around it.
    private static readonly string[] Sensitive =
    [
        "Patient {PERSON} (MRN {MRN}) was seen on {DATE} for a follow-up visit.",
        "To reschedule, call {PHONE} or write to {EMAIL}.",
        "SSN {SSN} is on file and the copay was charged to card {CREDIT_CARD}.",
        "Referred by Dr. {SURNAME} (NPI {NPI}) after the visit on {DATE}.",
        "The patient portal was opened from {IP} using {URL} yesterday.",
        "Mother {PERSON} can be reached at {PHONE}.",
        "Discharge is planned for {DATE}; the next visit is {DATE}.",
        "Insurance contact: {EMAIL}.",
        "Seen by Dr. {SURNAME} today.",
    ];

    // Look-alikes that are NOT sensitive. A detector that flags these loses precision.
    private static readonly string[] Decoys =
    [
        "Vitals: BP 120/80 mmHg, pulse 72, temperature 98.6 F.",
        "Room 214B, order number 48213, ICD-10 code E11.9.",
        "Continue metformin 500 mg twice daily and recheck HbA1c in 3 months.",
        "Lab results: WBC 7.2, hemoglobin 13.5, glucose 104.",
        "No acute distress; lungs clear to auscultation.",
    ];

    private static readonly string[] FirstNames = ["John", "Mary", "Anna", "Robert", "Priya", "Carlos", "Linda", "Wei", "Fatima", "James"];
    private static readonly string[] LastNames = ["Smith", "Rao", "Johnson", "Garcia", "Nguyen", "Okafor", "Patel", "Miller", "Khan", "Brown"];
    private static readonly string[] MailDomains = ["example.org", "example.com", "mail.example.net"];

    private static readonly Regex Slot = new(@"\{([A-Z_]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<GeneratedCase> Generate(int count, int seed = DefaultSeed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var rng = new DeterministicRandom(seed);
        var cases = new List<GeneratedCase>(count);
        for (var i = 0; i < count; i++) cases.Add(Note(rng));
        return cases;
    }

    private static GeneratedCase Note(DeterministicRandom rng)
    {
        List<string> sentences;
        if (rng.Next(100) < 10)
            sentences = Pick(Decoys, 2, rng);   // a note with nothing to find
        else
        {
            sentences = Pick(Sensitive, 2 + rng.Next(2), rng);
            sentences.Insert(rng.Next(sentences.Count + 1), Pick(Decoys, 1, rng)[0]);
        }

        var text = new StringBuilder();
        var spans = new List<LabeledSpan>();
        foreach (var sentence in sentences)
        {
            if (text.Length > 0) text.Append(' ');
            Render(sentence, text, spans, rng);
        }
        return new GeneratedCase(text.ToString(), spans);
    }

    private static List<string> Pick(string[] pool, int count, DeterministicRandom rng)
    {
        var left = pool.ToList();
        var picked = new List<string>();
        for (var i = 0; i < count && left.Count > 0; i++)
        {
            var k = rng.Next(left.Count);
            picked.Add(left[k]);
            left.RemoveAt(k);
        }
        return picked;
    }

    private static void Render(string template, StringBuilder text, List<LabeledSpan> spans, DeterministicRandom rng)
    {
        var last = 0;
        foreach (Match m in Slot.Matches(template))
        {
            text.Append(template, last, m.Index - last);
            var (type, value) = Make(m.Groups[1].Value, rng);
            spans.Add(new LabeledSpan(type, text.Length, text.Length + value.Length));
            text.Append(value);
            last = m.Index + m.Length;
        }
        text.Append(template, last, template.Length - last);
    }

    private static (string Type, string Value) Make(string slot, DeterministicRandom rng) => slot switch
    {
        "PERSON" => (EntityTypes.Person, $"{rng.Pick(FirstNames)} {rng.Pick(LastNames)}"),
        "SURNAME" => (EntityTypes.Person, rng.Pick(LastNames)),
        "SSN" => (EntityTypes.Ssn, $"{rng.Next(100, 666):D3}-{rng.Next(1, 100):D2}-{rng.Next(1, 10000):D4}"),
        "MRN" => (EntityTypes.Mrn, rng.Next(1_000_000, 10_000_000).ToString()),
        "PHONE" => (EntityTypes.Phone, rng.Next(2) == 0
            ? $"({rng.Next(200, 1000)}) {rng.Next(200, 1000)}-{rng.Next(0, 10000):D4}"
            : $"{rng.Next(200, 1000)}-{rng.Next(200, 1000)}-{rng.Next(0, 10000):D4}"),
        "EMAIL" => (EntityTypes.Email, $"{rng.Pick(FirstNames).ToLowerInvariant()}.{rng.Pick(LastNames).ToLowerInvariant()}@{rng.Pick(MailDomains)}"),
        "DATE" => (EntityTypes.Date, rng.Next(2) == 0
            ? $"{rng.Next(1, 13):D2}/{rng.Next(1, 29):D2}/{rng.Next(1985, 2026)}"
            : $"{rng.Next(1985, 2026)}-{rng.Next(1, 13):D2}-{rng.Next(1, 29):D2}"),
        "CREDIT_CARD" => (EntityTypes.CreditCard, Card(rng)),
        "NPI" => (EntityTypes.Npi, Npi(rng)),
        "IP" => (EntityTypes.IpAddress, $"{rng.Next(1, 224)}.{rng.Next(0, 256)}.{rng.Next(0, 256)}.{rng.Next(1, 255)}"),
        "URL" => (EntityTypes.Url, $"https://portal.example.org/patients/{rng.Next(10000, 99999)}"),
        _ => throw new InvalidOperationException($"Unknown placeholder {slot}."),
    };

    /// <summary>16 digits starting with 4, with a correct Luhn check digit, written in groups of four.</summary>
    private static string Card(DeterministicRandom rng)
    {
        var digits = new StringBuilder("4");
        for (var i = 0; i < 14; i++) digits.Append((char)('0' + rng.Next(10)));
        digits.Append((char)('0' + LuhnCheckDigit(digits.ToString())));
        var n = digits.ToString();
        return $"{n[..4]} {n[4..8]} {n[8..12]} {n[12..]}";
    }

    /// <summary>10 digits starting with 1 or 2. Its check digit is computed as if the number were prefixed with 80840.</summary>
    private static string Npi(DeterministicRandom rng)
    {
        var nine = new StringBuilder(rng.Next(2) == 0 ? "1" : "2");
        for (var i = 0; i < 8; i++) nine.Append((char)('0' + rng.Next(10)));
        return nine.ToString() + LuhnCheckDigit("80840" + nine);
    }

    /// <summary>The digit that, appended to <paramref name="partial"/>, makes the whole number pass the Luhn check.</summary>
    private static int LuhnCheckDigit(string partial)
    {
        var sum = 0;
        var doubleIt = true;   // the last digit of the partial number sits next to the check digit, so it is the first one doubled
        for (var i = partial.Length - 1; i >= 0; i--)
        {
            var d = partial[i] - '0';
            if (doubleIt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubleIt = !doubleIt;
        }
        return (10 - sum % 10) % 10;
    }
}

/// <summary>
/// SplitMix64: a few lines, fully specified, and it never changes. <c>System.Random</c> is not promised to give the same numbers
/// for the same seed in a future .NET, which would silently change every dataset.
/// </summary>
internal sealed class DeterministicRandom(int seed)
{
    private ulong _state = unchecked((ulong)seed);

    public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    public string Pick(string[] items) => items[Next(items.Length)];

    private ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}