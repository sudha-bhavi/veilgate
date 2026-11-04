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
        public void Add(User user) => db.Users.Add(user);

    public Task<string?> GetTenantNameAsync(CancellationToken ct)
        => db.Tenants.AsNoTracking().Select(t => t.Name).FirstOrDefaultAsync(ct); // row-level security leaves only the caller's own tenant

    public async Task<(IReadOnlyList<UserListRow> Items, int Total)> ListAsync(
        UserRole? role, UserStatus? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (role is { } r) query = query.Where(u => u.Role == r);
        if (status is { } s) query = query.Where(u => u.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The search text is data, not a pattern: escape LIKE's own wildcards.
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(u => EF.Functions.ILike(u.DisplayName, pattern, "\\") || EF.Functions.ILike(u.Email, pattern, "\\"));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new { User = u, HasMfa = db.MfaFactors.Any(f => f.UserId == u.Id && f.IsConfirmed) })
            .ToListAsync(ct);
        return (rows.Select(x => new UserListRow(x.User, x.HasMfa)).ToList(), total);
    }

    public async Task<bool> TryReserveLoginAttemptAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        // One statement does the whole job, so concurrent requests queue on the row lock and each sees the count the previous one left.
        // The WHERE clause is the "may this attempt go on?" test; the CASEs lock the account on the fifth attempt.
        var lockSeconds = User.LockDuration.TotalSeconds;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE users SET
                failed_attempts = CASE WHEN failed_attempts + 1 >= {User.MaxFailedAttempts} THEN 0 ELSE failed_attempts + 1 END,
                locked_until    = CASE WHEN failed_attempts + 1 >= {User.MaxFailedAttempts} THEN {now} + make_interval(secs => {lockSeconds}) ELSE locked_until END,
                updated_at      = {now}
            WHERE id = {user.Id} AND (locked_until IS NULL OR locked_until <= {now})", ct);
        await db.Entry(user).ReloadAsync(ct);   // so IsLocked, and the later "reset on success", work from the real values
        return changed == 1;
    }

    public Task<int> CountActiveAdminsExceptAsync(Guid exceptUserId, CancellationToken ct)
        => db.Users.CountAsync(u => u.Role == UserRole.TenantAdmin && u.Status == UserStatus.Active && u.Id != exceptUserId, ct);
}

internal sealed class LoginCandidateRow
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
}
