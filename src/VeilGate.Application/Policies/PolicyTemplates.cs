using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

/// <summary>
/// The starting-point policies offered when creating a policy (Backend doc 4.3: blank, hipaa-safe-harbor, names-only).
/// They are written in the same YAML a user would write and go through the same parser, so a template can never be
/// something the parser would reject.
/// </summary>
public static class PolicyTemplates
{
    public const string Blank = "blank";
    public const string HipaaSafeHarbor = "hipaa-safe-harbor";
    public const string NamesOnly = "names-only";

    private static readonly Dictionary<string, string> Yaml = new()
    {
        [Blank] = """
            version: 1
            name: blank
            defaults: { action: REDACT, minConfidence: 0.5 }
            output: { scan: true, onLeak: REDACT }
            """,

        // Database doc 7.1: identifiers redacted, people and addresses tokenized (reversible), contact details masked, dates shifted.
        [HipaaSafeHarbor] = """
            version: 1
            name: hipaa-safe-harbor-v1
            defaults: { action: REDACT, minConfidence: 0.5 }
            entities:
              SSN:         { action: REDACT }
              MRN:         { action: REDACT }
              CREDIT_CARD: { action: REDACT }
              NPI:         { action: REDACT }
              DEA:         { action: REDACT }
              PERSON:      { action: TOKENIZE }
              ADDRESS:     { action: TOKENIZE }
              PHONE:       { action: MASK }
              EMAIL:       { action: MASK }
              DOB:         { action: SHIFT, maxDays: 365 }
              DATE:        { action: SHIFT, maxDays: 365 }
            reidentify: { enabled: true, ttlSeconds: 3600 }
            output: { scan: true, onLeak: REDACT }
            limits: { maxTextChars: 32768 }
            """,

        // Only people are hidden; everything else passes. A deliberately light policy for low-risk text.
        [NamesOnly] = """
            version: 1
            name: names-only
            defaults: { action: ALLOW, minConfidence: 0.5 }
            entities:
              PERSON: { action: TOKENIZE }
            reidentify: { enabled: true, ttlSeconds: 3600 }
            output: { scan: false, onLeak: REDACT }
            """,
    };

    private static readonly Lazy<IReadOnlyDictionary<string, PolicyDocument>> Parsed = new(() =>
        Yaml.ToDictionary(kv => kv.Key, kv =>
        {
            var result = PolicyParser.Parse(kv.Value);
            return result.IsValid
                ? result.Policy!
                : throw new InvalidOperationException($"Built-in policy template '{kv.Key}' is invalid: {result.Errors[0].Message}");
        }));

    public static IReadOnlyCollection<string> Names => Yaml.Keys;

    public static string YamlOf(string name) => Yaml[name];

    public static PolicyDocument? Get(string name) => Parsed.Value.TryGetValue(name, out var doc) ? doc : null;
}
