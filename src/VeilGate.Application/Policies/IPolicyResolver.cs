using VeilGate.Domain.Common;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

/// <summary>The policy to apply to a request, and which version of it that is (reported back to the caller and recorded in the audit log).</summary>
public sealed record ResolvedPolicy(PolicyDocument Document, int Version);

public interface IPolicyResolver
{
    /// <summary>
    /// Picks the policy for a request: the one the caller asked for (it must belong to the caller's tenant), otherwise the
    /// API client's default. A policy that is missing and one that belongs to another tenant look the same: not found.
    /// </summary>
    Task<Result<ResolvedPolicy>> ResolveAsync(Guid tenantId, Guid clientId, Guid? policyId, CancellationToken ct = default);
}

/// <summary>
/// Until policies are stored and approved in the database (M4), every request uses the built-in HIPAA Safe Harbor template.
/// A specific policy id cannot exist yet, so asking for one is "not found".
/// </summary>
public sealed class BuiltInPolicyResolver : IPolicyResolver
{
    public Task<Result<ResolvedPolicy>> ResolveAsync(Guid tenantId, Guid clientId, Guid? policyId, CancellationToken ct = default)
    {
        if (policyId is not null) return Task.FromResult<Result<ResolvedPolicy>>(Error.NotFound());
        var policy = PolicyTemplates.Get(PolicyTemplates.HipaaSafeHarbor)!;
        return Task.FromResult<Result<ResolvedPolicy>>(new ResolvedPolicy(policy, Version: 1));
    }
}
