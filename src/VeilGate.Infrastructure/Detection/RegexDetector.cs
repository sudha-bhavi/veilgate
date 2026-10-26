using System.Text.RegularExpressions;
using VeilGate.Application.Detection;
using VeilGate.Domain.Detection;

namespace VeilGate.Infrastructure.Detection;

public sealed class RegexDetector : IDetector
{
    private sealed record Rule(string Type, Regex Pattern, Func<string, bool>? Validator, double Confidence, int Priority);

    // A regex that runs too long can freeze the server (an attack called ReDoS). 100 ms matches doc 03, section 4.4.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private static Regex R(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, Timeout);

    private static readonly Rule[] Rules =
    [
        new(EntityTypes.Ssn, R(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)"), Checksums.Ssn, 0.95, 120),
        new(EntityTypes.CreditCard, R(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)"), Checksums.CreditCard, 0.95, 120),
        new(EntityTypes.Email, R(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"), null, 1.0, 100),
        new(EntityTypes.Phone, R(@"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?!\d)"), null, 0.9, 100),
        new(EntityTypes.IpAddress, R(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b"), null, 0.95, 100),
        new(EntityTypes.Url, R(@"\bhttps?://[^\s<>""']+"), null, 0.95, 100),
        new(EntityTypes.Date, R(@"\b(?:0?[1-9]|1[0-2])/(?:0?[1-9]|[12]\d|3[01])/(?:\d{4}|\d{2})\b"), null, 0.9, 100),
        new(EntityTypes.Date, R(@"\b\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])\b"), null, 0.9, 100),
        // The label ("MRN") is not part of the secret, only the number is. The named group "v" marks the part to report.
        new(EntityTypes.Mrn, R(@"\bMRN[:#\s]*(?<v>\d{5,10})\b", RegexOptions.IgnoreCase), null, 0.95, 110),
        new(EntityTypes.Npi, R(@"\bNPI[:#\s]*(?<v>\d{10})\b", RegexOptions.IgnoreCase), Checksums.Npi, 0.95, 110),
    ];

    public string Name => "regex";

    public IEnumerable<DetectedEntity> Detect(string text, DetectionContext context)
    {
        foreach (var rule in Rules)
        {
            foreach (Match m in rule.Pattern.Matches(text))
            {
                // If the pattern has a group named "v", report only that part; otherwise the whole match.
                var part = m.Groups["v"].Success ? m.Groups["v"] : m;
                if (rule.Validator is not null && !rule.Validator(part.Value)) continue;
                yield return new DetectedEntity(rule.Type, part.Index, part.Index + part.Length, rule.Confidence, Name, rule.Priority);
            }
        }
    }
}