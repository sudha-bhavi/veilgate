using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

public sealed record LoginCandidate(Guid UserId, Guid TenantId);
public sealed record UserTokenState(int TokenVersion, UserStatus Status);

public interface IUserRepository
{
    /// <summary>Finds a user by email across tenants. Used only by login, before the tenant is known.</summary>
    Task<LoginCandidate?> FindLoginCandidateAsync(string email, CancellationToken ct);

    /// <summary>Loads a user of the current tenant (the tenant must already be set; row-level security enforces it).</summary>
    Task<User?> GetCurrentTenantUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Cheap check run on every authenticated request: has the token been invalidated?</summary>
    Task<UserTokenState?> GetTokenStateAsync(Guid userId, CancellationToken ct);

    Task<bool> IsTenantActiveAsync(CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
