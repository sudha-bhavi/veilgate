using FluentValidation;
using VeilGate.Application.ApiClients;
using VeilGate.Domain.ApiClients;

namespace VeilGate.Api.ApiClients;

// The JSON bodies of the API client endpoints (Backend doc 5.1). They hold only what the caller may choose: never a tenant id,
// an owner or a status. Those come from the verified token, and an unknown property is rejected outright.

public sealed record CreateApiClientRequest(
    string Name, string[] Scopes, Guid? DefaultPolicyId, string[]? IpAllowlist, int? RateLimitPerMinute);

/// <summary>Every field is optional; a field that is left out stays as it is. An empty ipAllowlist means "from anywhere".</summary>
public sealed record UpdateApiClientRequest(string[]? Scopes, string[]? IpAllowlist, int? RateLimitPerMinute, Guid? DefaultPolicyId);

public sealed record RotateSecretRequest(int GraceHours);

internal static class ApiClientRules
{
    public static void Scopes<T>(AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, IEnumerable<string>?>> scopes)
    {
        v.RuleForEach(scopes).Must(s => s is not null && ApiClient.AllowedScopes.Contains(s)).WithMessage("Unknown scope");
    }

    public static void IpAllowlist<T>(AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, IEnumerable<string>?>> list)
    {
        v.RuleForEach(list).Must(ip => ApiClient.TryNormalizeCidr(ip, out _)).WithMessage("Invalid CIDR");
    }
}

public sealed class CreateApiClientRequestValidator : AbstractValidator<CreateApiClientRequest>
{
    public CreateApiClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required").MaximumLength(80).WithMessage("Maximum 80 characters");
        RuleFor(x => x.Scopes).NotEmpty().WithMessage("At least one scope is required");
        ApiClientRules.Scopes(this, x => x.Scopes);
        RuleFor(x => x.IpAllowlist!.Length).LessThanOrEqualTo(ApiClient.MaxIpAllowlistEntries)
            .OverridePropertyName("ipAllowlist").WithMessage($"At most {ApiClient.MaxIpAllowlistEntries} networks").When(x => x.IpAllowlist is not null);
        ApiClientRules.IpAllowlist(this, x => x.IpAllowlist);
        RuleFor(x => x.RateLimitPerMinute!.Value).InclusiveBetween(1, ApiClient.MaxRateLimitPerMinute)
            .OverridePropertyName("rateLimitPerMinute").WithMessage($"Must be between 1 and {ApiClient.MaxRateLimitPerMinute}").When(x => x.RateLimitPerMinute is not null);
    }
}

public sealed class UpdateApiClientRequestValidator : AbstractValidator<UpdateApiClientRequest>
{
    public UpdateApiClientRequestValidator()
    {
        RuleFor(x => x).Must(x => x.Scopes is not null || x.IpAllowlist is not null || x.RateLimitPerMinute is not null || x.DefaultPolicyId is not null)
            .OverridePropertyName("body").WithMessage("Give at least one field to change");
        RuleFor(x => x.Scopes).NotEmpty().WithMessage("At least one scope is required").When(x => x.Scopes is not null);
        ApiClientRules.Scopes(this, x => x.Scopes);
        RuleFor(x => x.IpAllowlist!.Length).LessThanOrEqualTo(ApiClient.MaxIpAllowlistEntries)
            .OverridePropertyName("ipAllowlist").WithMessage($"At most {ApiClient.MaxIpAllowlistEntries} networks").When(x => x.IpAllowlist is not null);
        ApiClientRules.IpAllowlist(this, x => x.IpAllowlist);
        RuleFor(x => x.RateLimitPerMinute!.Value).InclusiveBetween(1, ApiClient.MaxRateLimitPerMinute)
            .OverridePropertyName("rateLimitPerMinute").WithMessage($"Must be between 1 and {ApiClient.MaxRateLimitPerMinute}").When(x => x.RateLimitPerMinute is not null);
    }
}

public sealed class RotateSecretRequestValidator : AbstractValidator<RotateSecretRequest>
{
    public RotateSecretRequestValidator() =>
        RuleFor(x => x.GraceHours).Must(h => RotateSecretHandler.AllowedGraceHours.Contains(h)).WithMessage("Must be 1, 24 or 168");
}
