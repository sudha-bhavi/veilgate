using VeilGate.Application.Policies;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Abstractions;

public interface IPolicyRepository
{
    void AddPolicy(Policy policy);
    void AddVersion(PolicyVersion version);

    /// <summary>Null when the policy does not exist or belongs to another tenant (row-level security hides it).</summary>
    Task<Policy?> FindAsync(Guid policyId, CancellationToken ct);
    Task<PolicyVersion?> FindVersionAsync(Guid versionId, CancellationToken ct);
    Task<PolicyVersion?> FindVersionByNumberAsync(Guid policyId, int versionNo, CancellationToken ct);
    Task<bool> NameExistsAsync(string name, CancellationToken ct);

    /// <summary>One page of policies, by name. <paramref name="status"/> is one of <see cref="PolicyStatus"/> or null for all.</summary>
    Task<PolicyPage> ListAsync(string? status, string? search, int page, int pageSize, CancellationToken ct);

    /// <summary>Versions of one policy, newest first.</summary>
    Task<(IReadOnlyList<PolicyVersion> Items, int Total)> ListVersionsAsync(Guid policyId, int page, int pageSize, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
