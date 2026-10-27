using VeilGate.Domain.Common;

namespace VeilGate.Domain.Evaluation;

/// <summary>One synthetic note and the sensitive values it really contains.</summary>
public sealed class EvalCase : TenantEntity
{
    public const int MaxTextLength = 20_000;

    private EvalCase() { }

    public Guid DatasetId { get; private set; }
    public int Ordinal { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public LabeledSpan[] Expected { get; private set; } = [];

    public static Result<EvalCase> Create(Guid tenantId, Guid datasetId, int ordinal, string text, IEnumerable<LabeledSpan> expected)
    {
        if (tenantId == Guid.Empty || datasetId == Guid.Empty) return Error.Validation("validation.failed", "Tenant and dataset are required.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return Error.Validation("validation.failed", $"A note is 1 to {MaxTextLength} characters.");

        var spans = expected.ToArray();
        // A label that points outside the note would silently skew every score, so refuse it up front.
        if (spans.Any(s => string.IsNullOrWhiteSpace(s.Type) || s.Start < 0 || s.End <= s.Start || s.End > text.Length))
            return Error.Validation("validation.failed", "A label lies outside the note.");

        var item = new EvalCase { DatasetId = datasetId, Ordinal = ordinal, Text = text, Expected = spans };
        item.SetTenant(tenantId);
        return item;
    }
}