using FluentValidation;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Api.Evaluation;

// The JSON bodies of the dataset and evaluation endpoints (Backend doc 5.3). The tenant comes from the verified token, never the body.

/// <param name="Count">How many notes. Default 200.</param>
/// <param name="Seed">The same seed gives the same notes. Default 1337.</param>
public sealed record CreateSyntheticDatasetRequest(string Name, int? Count, int? Seed);

public sealed class CreateSyntheticDatasetRequestValidator : AbstractValidator<CreateSyntheticDatasetRequest>
{
    public CreateSyntheticDatasetRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required")
            .MaximumLength(EvalDataset.MaxNameLength).WithMessage($"Maximum {EvalDataset.MaxNameLength} characters");
        RuleFor(x => x.Count).Must(c => c is null or (>= 1 and <= EvalDataset.MaxCases))
            .WithMessage($"Between 1 and {EvalDataset.MaxCases} notes");
    }
}

/// <param name="PolicyVersion">Leave out for the published version (or the draft, if nothing is published yet).</param>
/// <param name="Mode">"rules" (default) or "rules+ner".</param>
/// <param name="RecallThreshold">0 to 1, default 0.98.</param>
public sealed record CreateEvaluationRequest(Guid DatasetId, Guid PolicyId, int? PolicyVersion, string? Mode, double? RecallThreshold);

public sealed class CreateEvaluationRequestValidator : AbstractValidator<CreateEvaluationRequest>
{
    public CreateEvaluationRequestValidator()
    {
        RuleFor(x => x.DatasetId).NotEmpty().WithMessage("Dataset is required");
        RuleFor(x => x.PolicyId).NotEmpty().WithMessage("Policy is required");
        RuleFor(x => x.PolicyVersion).Must(v => v is null or >= 1).WithMessage("Version numbers start at 1");
        RuleFor(x => x.Mode).Must(m => m is null || EvalRun.Modes.IsValid(m)).WithMessage("Mode must be rules or rules+ner");
        RuleFor(x => x.RecallThreshold).Must(t => t is null or (>= 0 and <= 1)).WithMessage("Between 0 and 1");
    }
}