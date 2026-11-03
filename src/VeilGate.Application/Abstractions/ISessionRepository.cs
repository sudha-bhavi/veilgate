using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

public interface ISessionRepository
{
    void Add(Session session);
        /// <summary>Runs BEFORE a tenant is set: finds which tenant owns this refresh token, through the lookup function.</summary>
    Task<Guid?> FindTenantByRefreshHashAsync(string refreshHash, CancellationToken ct);

    /// <summary>Finds the session whose current OR previous refresh hash matches. Previous matters: it is how reuse is caught.</summary>
    Task<Session?> FindByRefreshHashAsync(string refreshHash, CancellationToken ct);

    Task<List<Session>> ListActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>One of this user's own sessions by id, or null. Someone else's session is null too, so its existence is not revealed.</summary>
    Task<Session?> FindForUserAsync(Guid sessionId, Guid userId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}