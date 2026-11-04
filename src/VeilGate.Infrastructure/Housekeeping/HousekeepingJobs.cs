using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Housekeeping;

public sealed class HousekeepingOptions
{
    public const string Section = "Housekeeping";

    /// <summary>How often expired vault sessions are removed (Backend doc 6.6: every minute).</summary>
    public int VaultPurgeSeconds { get; set; } = 60;

    /// <summary>How often ended login sessions are removed (Backend doc 6.6: every 5 minutes).</summary>
    public int SessionReapSeconds { get; set; } = 300;

    /// <summary>How long an ended login session is kept, so a replayed old refresh token is still recognised as reuse.</summary>
    public int SessionGraceHours { get; set; } = 24;
}

/// <summary>The two cleanup jobs. Each opens its own scope, so a failure in one run never leaves a half-used database context behind.</summary>
public sealed class HousekeepingJobs(IServiceScopeFactory scopes, IOptions<HousekeepingOptions> options, ILogger<HousekeepingJobs> logger)
{
    public async Task<int> PurgeVaultAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var removed = await scope.ServiceProvider.GetRequiredService<IHousekeepingStore>().PurgeExpiredVaultAsync(ct);
        if (removed > 0) logger.LogInformation("Removed {Count} expired vault sessions", removed);
        return removed;
    }

    public async Task<int> ReapSessionsAsync(CancellationToken ct)
    {
        var grace = TimeSpan.FromHours(Math.Max(options.Value.SessionGraceHours, 0));
        await using var scope = scopes.CreateAsyncScope();
        var removed = await scope.ServiceProvider.GetRequiredService<IHousekeepingStore>().ReapSessionsAsync(Session.IdleTimeout, grace, ct);
        if (removed > 0) logger.LogInformation("Removed {Count} ended login sessions", removed);
        return removed;
    }
}
