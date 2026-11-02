using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Persistence;

public sealed class InvitationRepository(VeilGateDbContext db) : IInvitationRepository
{
    // No tenant filter: row-level security already limits every query to the current tenant.
    public void Add(Invitation invitation) => db.Invitations.Add(invitation);

    public async Task<Guid?> FindTenantByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        // The one invitation query that runs before a tenant is known. EF's scalar SqlQuery needs the column named "Value".
        var rows = await db.Database
            .SqlQuery<Guid?>($"select auth_find_invitation_tenant({tokenHash}) as \"Value\"")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public Task<Invitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);

    public Task<List<Invitation>> ListForUserAsync(Guid userId, CancellationToken ct) =>
        db.Invitations.Where(i => i.UserId == userId).ToListAsync(ct);
}