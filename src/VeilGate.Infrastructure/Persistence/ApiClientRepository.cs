using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.ApiClients;

namespace VeilGate.Infrastructure.Persistence;

public sealed class ApiClientRepository(VeilGateDbContext db) : IApiClientRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void Add(ApiClient client) => db.ApiClients.Add(client);
    public void AddSecret(ApiClientSecret secret) => db.ApiClientSecrets.Add(secret);

    public async Task<Guid?> FindTenantByClientIdAsync(string clientId, CancellationToken ct)
    {
        // The one query that runs before a tenant is known. EF's scalar SqlQuery needs the column named "Value".
        var rows = await db.Database
            .SqlQuery<Guid?>($"select auth_find_client_tenant({clientId}) as \"Value\"")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public Task<ApiClient?> FindByClientIdAsync(string clientId, CancellationToken ct) =>
        db.ApiClients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

    public Task<ApiClient?> FindAsync(Guid id, CancellationToken ct) =>
        db.ApiClients.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<ApiClientState?> GetStateAsync(Guid id, CancellationToken ct) =>
        db.ApiClients.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new ApiClientState(c.Status, c.IpAllowlist, c.DefaultPolicyId))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ApiClientSecret>> ListSecretsAsync(Guid clientPk, CancellationToken ct) =>
        await db.ApiClientSecrets.Where(s => s.ClientPk == clientPk).OrderBy(s => s.CreatedAt).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string name, CancellationToken ct) =>
        db.ApiClients.AnyAsync(c => c.Name == name, ct);

    public async Task<(IReadOnlyList<ApiClient> Items, int Total)> ListAsync(
        Guid? ownerUserId, string? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.ApiClients.AsNoTracking();
        if (ownerUserId is { } owner) query = query.Where(c => c.OwnerUserId == owner);
        if (Enum.TryParse<ApiClientStatus>(status, ignoreCase: false, out var parsed)) query = query.Where(c => c.Status == parsed);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The search text is data, not a pattern: escape LIKE's own wildcards.
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern, "\\"));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
