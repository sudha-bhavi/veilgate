using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

/// <summary>
/// Serves the published version of a stored policy. Everything that is not "a published, intact, valid policy of this tenant"
/// is refused: a missing, foreign, archived or never-published policy is "not found", and a stored policy that no longer
/// matches its hash or no longer parses is an error. It is never silently swapped for another policy.
/// When the request names no policy, the API client's default policy is used; a client with no default gets the built-in template.
/// </summary>
public sealed class StoredPolicyResolver(IPolicyRepository policies, IApiClientRepository clients, BuiltInPolicyResolver fallback) : IPolicyResolver
{
    public async Task<Result<ResolvedPolicy>> ResolveAsync(Guid tenantId, Guid clientId, Guid? policyId, CancellationToken ct = default)
    {
        if (policyId is not null) return await LoadAsync(tenantId, policyId.Value, ct);

        // For a person using the playground the "client id" is their user id, so no client is found and the built-in template applies.
        var client = await clients.GetStateAsync(clientId, ct);
        if (client?.DefaultPolicyId is not { } defaultPolicyId) return await fallback.ResolveAsync(tenantId, clientId, null, ct);

        // A client that was set up to use a policy must never quietly run under a different one, so a default that
        // cannot be served (archived, never published) is an error that says so, not a fall back to the built-in template.
        var resolved = await LoadAsync(tenantId, defaultPolicyId, ct);
        return resolved.IsSuccess || resolved.Error.Code == "policy.invalid"
            ? resolved
            : Error.Conflict("policy.default_unavailable", "This client's default policy is not available.");
    }

    private async Task<Result<ResolvedPolicy>> LoadAsync(Guid tenantId, Guid policyId, CancellationToken ct)
    {
        var policy = await policies.FindAsync(policyId, ct);
        if (policy is null || policy.TenantId != tenantId || policy.IsArchived || policy.CurrentVersionId is null)
            return Error.NotFound();

        var version = await policies.FindVersionAsync(policy.CurrentVersionId.Value, ct);
        if (version is null || version.TenantId != tenantId || version.State != PolicyState.Published)
            return Error.NotFound();

        if (!version.IsIntact) return Broken();
        var parsed = PolicyParser.Parse(version.Definition);
        if (!parsed.IsValid) return Broken();

        return new ResolvedPolicy(parsed.Policy!, version.VersionNo);
    }

    private static Error Broken() => new("policy.invalid", "The published policy could not be loaded.");
}
