using VeilGate.Domain;

namespace VeilGate.Application.Abstractions;

public interface ISessionRepository
{
    void Add(Session session);

    /// <summary>Finds the session whose current OR previous refresh hash matches. Previous matters: it is how reuse is caught.</summary>
    Task<Session?> FindByRefreshHashAsync(string refreshHash, CancellationToken ct);

    Task<List<Session>> ListActiveForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}