using System.Text.RegularExpressions;
using VeilGate.Application.Detection;
using VeilGate.Domain.Detection;

namespace VeilGate.Infrastructure.Detection;

/// <summary>
/// Finds person names from context: after a title ("Dr. Rao") or after a label ("Patient John Smith", "Name: Mary O'Neil").
/// This is a stop-gap. Names have no fixed shape, so the real answer is the optional ONNX name-recognition model
/// (Backend doc 1.5), or a tenant dictionary of staff names. Confidence is deliberately modest (0.7) and the priority low,
/// so any rule-based detection of the same text (an MRN, a phone) wins.
/// </summary>
public sealed class NameHeuristicDetector : IDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    // One name word: starts with a capital, may contain an apostrophe or hyphen part ("O'Neil", "Anna-Maria").
    private const string Word = @"[A-Z][a-z]*(?:['-][A-Z]?[a-z]+)*";

    // After a title: one or two words.   "Dr. Rao", "Mrs Jane Doe"
    private static readonly Regex AfterTitle = new(
        $@"\b(?:Dr|Mr|Mrs|Ms|Miss|Prof)\.?\s+(?<v>{Word}(?:\s+{Word})?)",
        RegexOptions.CultureInvariant, Timeout);

    // After a label: two or three words, so "Patient was seen" and "Name: Smith" are ignored.   "Patient John Smith"
    // The label may be any case (the (?i: ) group); the name itself must be capitalised.
    private static readonly Regex AfterLabel = new(
        $@"\b(?i:patient|pt|name|client|member|mother|father|spouse|guardian)\b\s*[:\-]?\s*(?<v>{Word}(?:\s+{Word}){{1,2}})",
        RegexOptions.CultureInvariant, Timeout);

    public string Name => "heuristic";

    public IEnumerable<DetectedEntity> Detect(string text, DetectionContext context)
    {
        foreach (var regex in new[] { AfterTitle, AfterLabel })
            foreach (Match m in regex.Matches(text))
            {
                var name = m.Groups["v"];
                yield return new DetectedEntity(EntityTypes.Person, name.Index, name.Index + name.Length, 0.7, Name, Priority: 80);
            }
    }
}
