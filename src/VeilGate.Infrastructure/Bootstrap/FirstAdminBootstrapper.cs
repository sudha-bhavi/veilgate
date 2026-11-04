using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure.Bootstrap;

/// <summary>What the operator needs: the raw invitation token (to build the link) and when it stops working. It is shown once and never stored.</summary>
public sealed record BootstrapOutcome(Guid TenantId, Guid UserId, string InvitationToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Creates the very first organisation and its first Tenant Admin on a fresh database, so someone can sign in. The administrator has
/// NO password: they get one ordinary invitation (single use, 48 hours, only its hash is stored) and choose a password and an
/// authenticator when they open it. The same rules as every other invitation, so there is no second, weaker way in.
///
/// It works only while NO organisation exists, so it can never be used later to add an administrator by the back door. If the link is
/// lost before it was used, <see cref="ReissueAsync"/> replaces it, and only while that one administrator has still not signed up.
/// It runs with the schema owner's login (the one that can write across organisations), which only the operator holds.
/// </summary>
public sealed class FirstAdminBootstrapper(VeilGateDbContext db, IClock clock)
{
    public static readonly Error AlreadyDone = Error.Conflict("bootstrap.already_done",
        "An organisation already exists, so there is nothing to bootstrap. Administrators are invited from the console.");

    public static readonly Error NothingToReissue = Error.Conflict("bootstrap.nothing_to_reissue",
        "A new link can be made only while there is exactly one organisation with exactly one person who has not signed up yet.");

    private const string LockName = "veilgate-bootstrap";

    public async Task<Result<BootstrapOutcome>> RunAsync(string organisation, string adminEmail, string adminName, CancellationToken ct = default)
    {
        var tenant = Tenant.Create(organisation);
        if (tenant.IsFailure) return Result.Failure<BootstrapOutcome>(tenant.Error);

        var now = clock.UtcNow;
        var admin = User.Invite(tenant.Value.Id, adminEmail, adminName, UserRole.TenantAdmin, now);
        if (admin.IsFailure) return Result.Failure<BootstrapOutcome>(admin.Error);

        // The first administrator invites themselves: the invitation needs an inviter that exists.
        var invitation = Invitation.Create(tenant.Value.Id, admin.Value.Id, admin.Value.Email, UserRole.TenantAdmin, admin.Value.Id, now);
        if (invitation.IsFailure) return Result.Failure<BootstrapOutcome>(invitation.Error);

        // The organisation is new, so its audit chain starts here: event 1, after the all-zero "genesis" hash.
        var audit = AuditEvent.Create(tenant.Value.Id, now, "system", null, "tenant.bootstrap", "tenant", tenant.Value.Id, "success",
            details: new Dictionary<string, string> { ["role"] = nameof(UserRole.TenantAdmin) });
        if (audit.IsFailure) return Result.Failure<BootstrapOutcome>(audit.Error);
        audit.Value.Seal(1, AuditEvent.Genesis);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Two operators running this at the same moment: the second waits here, then finds the organisation and stops.
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtext({LockName}))", ct);
        if (await db.Tenants.AnyAsync(ct)) return Result.Failure<BootstrapOutcome>(AlreadyDone);

        db.Tenants.Add(tenant.Value);
        db.Users.Add(admin.Value);
        db.Invitations.Add(invitation.Value.Invitation);
        db.AuditEvents.Add(audit.Value);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new BootstrapOutcome(tenant.Value.Id, admin.Value.Id, invitation.Value.Token, invitation.Value.Invitation.ExpiresAt);
    }

    /// <summary>A fresh link for the one administrator who has not signed up yet. The old link stops working at once.</summary>
    public async Task<Result<BootstrapOutcome>> ReissueAsync(CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock(hashtext({LockName}))", ct);

        var tenants = await db.Tenants.Take(2).ToListAsync(ct);
        if (tenants.Count != 1) return Result.Failure<BootstrapOutcome>(NothingToReissue);

        var people = await db.Users.Where(u => u.TenantId == tenants[0].Id).Take(2).ToListAsync(ct);
        if (people.Count != 1 || people[0].Status != UserStatus.Invited) return Result.Failure<BootstrapOutcome>(NothingToReissue);

        var now = clock.UtcNow;
        var admin = people[0];
        foreach (var old in await db.Invitations.Where(i => i.UserId == admin.Id && i.AcceptedAt == null).ToListAsync(ct)) old.Revoke(now);

        var invitation = Invitation.Create(admin.TenantId, admin.Id, admin.Email, admin.Role, admin.Id, now);
        if (invitation.IsFailure) return Result.Failure<BootstrapOutcome>(invitation.Error);
        db.Invitations.Add(invitation.Value.Invitation);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new BootstrapOutcome(admin.TenantId, admin.Id, invitation.Value.Token, invitation.Value.Invitation.ExpiresAt);
    }
}
