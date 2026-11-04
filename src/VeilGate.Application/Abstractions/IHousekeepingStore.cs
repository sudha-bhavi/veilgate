namespace VeilGate.Application.Abstractions;

/// <summary>Cleans up finished data across every tenant. Each method returns how many rows it removed.</summary>
public interface IHousekeepingStore
{
    /// <summary>Removes vault sessions that have expired, and with them their tokens.</summary>
    Task<int> PurgeExpiredVaultAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes login sessions that ended (revoked, past their hard limit, or idle for longer than <paramref name="idleTimeout"/>)
    /// more than <paramref name="grace"/> ago.
    /// </summary>
    Task<int> ReapSessionsAsync(TimeSpan idleTimeout, TimeSpan grace, CancellationToken ct = default);
}
