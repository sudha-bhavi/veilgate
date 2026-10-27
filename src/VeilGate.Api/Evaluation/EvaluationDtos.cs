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