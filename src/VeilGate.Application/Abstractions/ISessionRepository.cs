using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

/// <summary>
/// Just enough of a session to decide whether an access token that points at it may still be used. The idle timeout is left out
/// on purpose: it only moves when a refresh happens, and an access token lives a few minutes at most.
/// </summary>
public sealed record SessionState(DateTimeOffset? RevokedAt, DateTimeOffset ExpiresAt)
{
    /// <summary>True once the session was ended (sign-out, revoke, password change, disable) or passed its hard limit.</summary>
    public bool IsEnded(DateTimeOffset now) => RevokedAt is not null || now >= ExpiresAt;
}

public interface ISessionRepository
{
    void Add(Session session);

    /// <summary>
    /// Swaps the session's refresh token in ONE atomic step: true only for the single caller whose presented token was still the current one.
    /// If eight requests present the same token at the same moment, exactly one gets true; the others get false and must be treated as
    /// a replay (each would otherwise be handed its own new token, and a stolen token would be as good as the real one). The default is the
    /// in-memory version, for hand-written test doubles (a mocking library replaces it, so tests that mock this interface set the method up
    /// themselves); the database repository replaces it with a conditional UPDATE.
    /// </summary>
    Task<bool> TryRotateAsync(Session session, string presentedHash, string newHash, DateTimeOffset now, CancellationToken ct) =>
        Task.FromResult(session.Rotate(presentedHash, newHash, now).IsSuccess);
        /// <summary>Runs BEFORE a tenant is set: finds which tenant owns this refresh token, through the lookup function.</summary>
    Task<Guid?> FindTenantByRefreshHashAsync(string refreshHash, CancellationToken ct);

    /// <summary>Finds the session whose current OR previous refresh hash matches. Previous matters: it is how reuse is caught.</summary>
    Task<Session?> FindByRefreshHashAsync(string refreshHash, CancellationToken ct);

    Task<List<Session>> ListActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Whether a session still stands, read on every signed-in request. Null when there is no such session (in this tenant).</summary>
    Task<SessionState?> GetStateAsync(Guid sessionId, CancellationToken ct);

    /// <summary>One of this user's own sessions by id, or null. Someone else's session is null too, so its existence is not revealed.</summary>
    Task<Session?> FindForUserAsync(Guid sessionId, Guid userId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}