using VeilGate.Domain.Common;

namespace VeilGate.Domain.Evaluation;

/// <summary>Where a sensitive value sits in a note, by position only: the note's text lives in <see cref="EvalCase"/>.</summary>
public sealed record LabeledSpan(string Type, int Start, int End);

/// <summary>A named collection of synthetic notes with known answers (Database doc 3.9). Never real patient data.</summary>
public sealed class EvalDataset : TenantEntity
{
    public const int MaxNameLength = 100;
    public const int MaxCases = 5_000;

    private EvalDataset() { }

    public string Name { get; private set; } = string.Empty;
    public int CaseCount { get; private set; }

    public static Result<EvalDataset> Create(Guid tenantId, string name, int caseCount)
    {
        if (tenantId == Guid.Empty) return Error.Validation("validation.failed", "Tenant is required.");
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length is < 1 or > MaxNameLength) return Error.Validation("validation.failed", $"Name must be 1 to {MaxNameLength} characters.");
        if (caseCount is < 1 or > MaxCases) return Error.Validation("validation.failed", $"A dataset has 1 to {MaxCases} notes.");

        var dataset = new EvalDataset { Name = clean, CaseCount = caseCount };
        dataset.SetTenant(tenantId);
        return dataset;
    }
}