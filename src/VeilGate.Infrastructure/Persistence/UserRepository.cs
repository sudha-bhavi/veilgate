using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class UserRepository(VeilGateDbContext db) : IUserRepository
{
    public async Task<LoginCandidate?> FindLoginCandidateAsync(string email, CancellationToken ct)
    {
        // The one query that runs before a tenant is known. It goes through the SECURITY DEFINER function from the migration.
        var rows = await db.Database
            .SqlQuery<LoginCandidateRow>($"select id as \"UserId\", tenant_id as \"TenantId\" from auth_find_user_by_email({email}::citext)")
            .Take(1) // email is globally unique, so there is at most one row
            .ToListAsync(ct);
        var row = rows.FirstOrDefault();
        return row is null ? null : new LoginCandidate(row.UserId, row.TenantId);
    }

    public Task<User?> GetCurrentTenantUserAsync(Guid userId, CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<UserTokenState?> GetTokenStateAsync(Guid userId, CancellationToken ct)
        => db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new UserTokenState(u.TokenVersion, u.Status))
            .FirstOrDefaultAsync(ct);

    public Task<bool> IsTenantActiveAsync(CancellationToken ct)
        => db.Tenants.AnyAsync(t => t.Status == "Active", ct); // row-level security leaves only the caller's own tenant

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

internal sealed class LoginCandidateRow
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
}
