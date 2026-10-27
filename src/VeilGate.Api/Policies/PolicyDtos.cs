using FluentValidation;
using VeilGate.Application.Policies;

namespace VeilGate.Api.Policies;

// The JSON bodies of the policy endpoints (Backend doc 4.3). They hold only what the caller may choose: never a tenant id,
// an author or an approver. Those come from the verified token, and an unknown property is rejected outright.

public sealed record CreatePolicyRequest(string Name, string Template, string? Description);

public sealed record SaveDraftRequest(string Yaml);

public sealed record SubmitPolicyRequest(string Summary);

public sealed record RejectPolicyRequest(string Reason);

public sealed record RollbackPolicyRequest(Guid VersionId);

public sealed record TestPolicyRequest(string Text);

public sealed class CreatePolicyRequestValidator : AbstractValidator<CreatePolicyRequest>
{
    public CreatePolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required").MaximumLength(80).WithMessage("Maximum 80 characters");
        RuleFor(x => x.Template).Must(t => t is not null && PolicyTemplates.Names.Contains(t))
            .WithMessage($"Must be one of: {string.Join(", ", PolicyTemplates.Names)}");
        RuleFor(x => x.Description).MaximumLength(500).WithMessage("Maximum 500 characters");
    }
}

public sealed class SaveDraftRequestValidator : AbstractValidator<SaveDraftRequest>
{
    public SaveDraftRequestValidator() =>
        RuleFor(x => x.Yaml).NotEmpty().WithMessage("Policy text is required")
            .MaximumLength(PolicyParser.MaxYamlChars).WithMessage($"Maximum {PolicyParser.MaxYamlChars} characters");
}

public sealed class SubmitPolicyRequestValidator : AbstractValidator<SubmitPolicyRequest>
{
    public SubmitPolicyRequestValidator() =>
        RuleFor(x => x.Summary).NotEmpty().WithMessage("Summary is required").Length(10, 500).WithMessage("Between 10 and 500 characters");
}

public sealed class RejectPolicyRequestValidator : AbstractValidator<RejectPolicyRequest>
{
    public RejectPolicyRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required").MaximumLength(500).WithMessage("Maximum 500 characters");
}

public sealed class RollbackPolicyRequestValidator : AbstractValidator<RollbackPolicyRequest>
{
    public RollbackPolicyRequestValidator() =>
        RuleFor(x => x.VersionId).NotEmpty().WithMessage("Version id is required");
}

public sealed class TestPolicyRequestValidator : AbstractValidator<TestPolicyRequest>
{
    public TestPolicyRequestValidator() =>
        RuleFor(x => x.Text).NotEmpty().WithMessage("Text is required").MaximumLength(32_768).WithMessage("Maximum 32768 characters");
}
