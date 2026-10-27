using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Policies;

namespace VeilGate.Infrastructure.Persistence;

public sealed class PolicyRepository(VeilGateDbContext db) : IPolicyRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void AddPolicy(Policy policy) => db.Policies.Add(policy);
    public void AddVersion(PolicyVersion version) => db.PolicyVersions.Add(version);

    public Task<Policy?> FindAsync(Guid policyId, CancellationToken ct) =>
        db.Policies.FirstOrDefaultAsync(p => p.Id == policyId, ct);

    public Task<PolicyVersion?> FindVersionAsync(Guid versionId, CancellationToken ct) =>
        db.PolicyVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}