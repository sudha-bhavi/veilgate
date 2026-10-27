using FluentValidation;
using VeilGate.Application.LlmProviders;
using VeilGate.Domain.LlmProviders;

namespace VeilGate.Api.LlmProviders;

// The JSON bodies of the provider endpoints (Backend doc 5.1). They hold only what an admin may choose: never a tenant id.
// The tenant comes from the verified token, and an unknown property is rejected outright. The API key goes in; it never comes out.

public sealed record CreateLlmProviderRequest(
    string Name, string BaseUrl, string ApiKey, string[] Models, int? TimeoutSeconds, int? Priority, bool? Enabled);

/// <summary>Name, address and models are always sent. Leave out apiKey to keep the stored key (not possible when the address changes).</summary>
public sealed record UpdateLlmProviderRequest(
    string Name, string BaseUrl, string? ApiKey, string[] Models, int? TimeoutSeconds, int? Priority, bool? Enabled);

public sealed record ReorderLlmProvidersRequest(Guid[] Ids);

public sealed class CreateLlmProviderRequestValidator : AbstractValidator<CreateLlmProviderRequest>
{
    public CreateLlmProviderRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required")
            .MaximumLength(LlmProvider.MaxNameLength).WithMessage($"Maximum {LlmProvider.MaxNameLength} characters");
        RuleFor(x => x.BaseUrl).NotEmpty().WithMessage("Address is required")
            .MaximumLength(LlmProvider.MaxBaseUrlLength).WithMessage($"Maximum {LlmProvider.MaxBaseUrlLength} characters");
        RuleFor(x => x.ApiKey).NotEmpty().WithMessage("API key is required")
            .Length(LlmProviderKeys.MinKeyLength, LlmProviderKeys.MaxKeyLength)
            .WithMessage($"Between {LlmProviderKeys.MinKeyLength} and {LlmProviderKeys.MaxKeyLength} characters");
        RuleFor(x => x.Models).NotEmpty().WithMessage("At least one model is required");
        RuleFor(x => x.Models.Length).LessThanOrEqualTo(LlmProvider.MaxModels).OverridePropertyName("models")
            .WithMessage($"At most {LlmProvider.MaxModels} models").When(x => x.Models is not null);
        RuleForEach(x => x.Models).Must(LlmProvider.IsValidModelName).WithMessage("Invalid model name");
        RuleFor(x => x.TimeoutSeconds!.Value).InclusiveBetween(1, LlmProvider.MaxTimeoutSeconds).OverridePropertyName("timeoutSeconds")
            .WithMessage($"Must be between 1 and {LlmProvider.MaxTimeoutSeconds}").When(x => x.TimeoutSeconds is not null);
        RuleFor(x => x.Priority!.Value).InclusiveBetween(0, 10_000).OverridePropertyName("priority")
            .WithMessage("Must be between 0 and 10000").When(x => x.Priority is not null);
    }
}

public sealed class UpdateLlmProviderRequestValidator : AbstractValidator<UpdateLlmProviderRequest>
{
    public UpdateLlmProviderRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required")
            .MaximumLength(LlmProvider.MaxNameLength).WithMessage($"Maximum {LlmProvider.MaxNameLength} characters");
        RuleFor(x => x.BaseUrl).NotEmpty().WithMessage("Address is required")
            .MaximumLength(LlmProvider.MaxBaseUrlLength).WithMessage($"Maximum {LlmProvider.MaxBaseUrlLength} characters");
        RuleFor(x => x.ApiKey!).Length(LlmProviderKeys.MinKeyLength, LlmProviderKeys.MaxKeyLength).OverridePropertyName("apiKey")
            .WithMessage($"Between {LlmProviderKeys.MinKeyLength} and {LlmProviderKeys.MaxKeyLength} characters").When(x => x.ApiKey is not null);
        RuleFor(x => x.Models).NotEmpty().WithMessage("At least one model is required");
        RuleFor(x => x.Models.Length).LessThanOrEqualTo(LlmProvider.MaxModels).OverridePropertyName("models")
            .WithMessage($"At most {LlmProvider.MaxModels} models").When(x => x.Models is not null);
        RuleForEach(x => x.Models).Must(LlmProvider.IsValidModelName).WithMessage("Invalid model name");
        RuleFor(x => x.TimeoutSeconds!.Value).InclusiveBetween(1, LlmProvider.MaxTimeoutSeconds).OverridePropertyName("timeoutSeconds")
            .WithMessage($"Must be between 1 and {LlmProvider.MaxTimeoutSeconds}").When(x => x.TimeoutSeconds is not null);
        RuleFor(x => x.Priority!.Value).InclusiveBetween(0, 10_000).OverridePropertyName("priority")
            .WithMessage("Must be between 0 and 10000").When(x => x.Priority is not null);
    }
}

public sealed class ReorderLlmProvidersRequestValidator : AbstractValidator<ReorderLlmProvidersRequest>
{
    public ReorderLlmProvidersRequestValidator() =>
        RuleFor(x => x.Ids).NotEmpty().WithMessage("List the providers in the order to try them");
}
