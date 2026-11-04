using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

public sealed record LoginCandidate(Guid UserId, Guid TenantId);
public sealed record UserTokenState(int TokenVersion, UserStatus Status);
public sealed record UserListRow(User User, bool HasMfa);

public interface IUserRepository
{
    /// <summary>Finds a user by email across tenants. Used only by login, before the tenant is known.</summary>
    Task<LoginCandidate?> FindLoginCandidateAsync(string email, CancellationToken ct);

    /// <summary>Loads a user of the current tenant (the tenant must already be set; row-level security enforces it).</summary>
    Task<User?> GetCurrentTenantUserAsync(Guid userId, CancellationToken ct);
    void Add(User user);

    /// <summary>The current tenant's name (row-level security leaves only the caller's own tenant). Used in invitation emails.</summary>
    Task<string?> GetTenantNameAsync(CancellationToken ct);

    /// <summary>One page of the tenant's users, by name. HasMfa is true when the person has a confirmed authenticator.</summary>
    Task<(IReadOnlyList<UserListRow> Items, int Total)> ListAsync(
        UserRole? role, UserStatus? status, string? search, int page, int pageSize, CancellationToken ct);

    /// <summary>How many Active Tenant Admins the tenant has, not counting one person. Used to protect the last admin.</summary>
    Task<int> CountActiveAdminsExceptAsync(Guid exceptUserId, CancellationToken ct);

    /// <summary>
    /// Reserves one sign-in attempt BEFORE the password or code is checked, in ONE atomic step: counts the attempt, locks the account when it
    /// was the fifth in a row, and says whether this attempt may go on to be checked (false when the account is already locked). Because the
    /// counting happens first and in the database, 100 requests sent at the same moment get exactly five checks between them; counting after
    /// the check (read, check, then add one) lets every one of them through. The user object is refreshed from the database afterwards. The
    /// default is the in-memory version, for hand-written test doubles (a mocking library replaces it, so tests that mock this interface set
    /// the method up themselves); the database repository replaces it with a single conditional UPDATE.
    /// </summary>
    Task<bool> TryReserveLoginAttemptAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        if (user.IsLocked(now)) return Task.FromResult(false);
        user.RegisterFailedLogin(now);
        return Task.FromResult(true);
    }

    /// <summary>Cheap check run on every authenticated request: has the token been invalidated?</summary>
    Task<UserTokenState?> GetTokenStateAsync(Guid userId, CancellationToken ct);

    Task<bool> IsTenantActiveAsync(CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
