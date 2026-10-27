using VeilGate.Domain.Policies;

namespace VeilGate.Application.Abstractions;

public interface IPolicyRepository
{
    void AddPolicy(Policy policy);
    void AddVersion(PolicyVersion version);

    /// <summary>Null when the policy does not exist or belongs to another tenant (row-level security hides it).</summary>
    Task<Policy?> FindAsync(Guid policyId, CancellationToken ct);
    Task<PolicyVersion?> FindVersionAsync(Guid versionId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}