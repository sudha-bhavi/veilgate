using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

/// <summary>
/// Serves the published version of a stored policy. Everything that is not "a published, intact, valid policy of this tenant"
/// is refused: a missing, foreign, archived or never-published policy is "not found", and a stored policy that no longer
/// matches its hash or no longer parses is an error. It is never silently swapped for another policy.
/// </summary>
public sealed class StoredPolicyResolver(IPolicyRepository policies, BuiltInPolicyResolver fallback) : IPolicyResolver
{
    public async Task<Result<ResolvedPolicy>> ResolveAsync(Guid tenantId, Guid clientId, Guid? policyId, CancellationToken ct = default)
    {
        // M4d adds the API client's default policy here. Until then "no policy asked for" means the built-in template.
        if (policyId is null) return await fallback.ResolveAsync(tenantId, clientId, null, ct);

        var policy = await policies.FindAsync(policyId.Value, ct);
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