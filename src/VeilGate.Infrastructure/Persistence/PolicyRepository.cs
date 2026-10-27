using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
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

    public Task<PolicyVersion?> FindVersionByNumberAsync(Guid policyId, int versionNo, CancellationToken ct) =>
        db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PolicyId == policyId && v.VersionNo == versionNo, ct);

    public Task<bool> NameExistsAsync(string name, CancellationToken ct) =>
        db.Policies.AnyAsync(p => p.Name == name, ct);

    public async Task<PolicyPage> ListAsync(string? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Policies.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // The search text is data, not a pattern: escape LIKE's own wildcards.
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern, "\\"));
        }

        // Same rule as PolicyStatus.Of: archived, else pending approval, else published, else draft.
        query = status switch
        {
            PolicyStatus.Archived => query.Where(p => p.ArchivedAt != null),
            PolicyStatus.PendingApproval => query.Where(p => p.ArchivedAt == null
                && db.PolicyVersions.Any(v => v.Id == p.DraftVersionId && v.State == PolicyState.PendingApproval)),
            PolicyStatus.Published => query.Where(p => p.ArchivedAt == null && p.CurrentVersionId != null
                && !db.PolicyVersions.Any(v => v.Id == p.DraftVersionId && v.State == PolicyState.PendingApproval)),
            PolicyStatus.Draft => query.Where(p => p.ArchivedAt == null && p.CurrentVersionId == null
                && !db.PolicyVersions.Any(v => v.Id == p.DraftVersionId && v.State == PolicyState.PendingApproval)),
            _ => query
        };

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var ids = rows.SelectMany(p => new[] { p.CurrentVersionId, p.DraftVersionId })
            .Where(id => id is not null).Select(id => id!.Value).ToList();
        var versions = await db.PolicyVersions.AsNoTracking()
            .Where(v => ids.Contains(v.Id))
            .Select(v => new { v.Id, v.VersionNo, v.State })
            .ToDictionaryAsync(v => v.Id, ct);

        var items = rows.Select(p =>
        {
            var current = p.CurrentVersionId is { } c ? versions.GetValueOrDefault(c) : null;
            var draft = p.DraftVersionId is { } d ? versions.GetValueOrDefault(d) : null;
            return new PolicyListItem(p.Id, p.Name,
                PolicyStatus.Of(p.IsArchived, current is not null, draft?.State),
                current?.VersionNo, draft is not null, p.CreatedAt);
        }).ToList();

        return new PolicyPage(items, total);
    }

    public async Task<(IReadOnlyList<PolicyVersion> Items, int Total)> ListVersionsAsync(Guid policyId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.PolicyVersions.AsNoTracking().Where(v => v.PolicyId == policyId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(v => v.VersionNo)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
