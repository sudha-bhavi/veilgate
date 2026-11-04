using Microsoft.Extensions.Options;
using VeilGate.Infrastructure.Housekeeping;

/// <summary>Deletes vault sessions (and their tokens) that have expired. Vault data is temporary by design.</summary>
public sealed class VaultPurgerWorker(HousekeepingJobs jobs, IOptions<HousekeepingOptions> options, ILogger<VaultPurgerWorker> log)
    : PeriodicWorker("vault purger", log)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.VaultPurgeSeconds);

    protected override Task RunOnceAsync(CancellationToken ct) => jobs.PurgeVaultAsync(ct);
}

/// <summary>Deletes login sessions that ended more than the grace period ago.</summary>
public sealed class SessionReaperWorker(HousekeepingJobs jobs, IOptions<HousekeepingOptions> options, ILogger<SessionReaperWorker> log)
    : PeriodicWorker("session reaper", log)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.SessionReapSeconds);

    protected override Task RunOnceAsync(CancellationToken ct) => jobs.ReapSessionsAsync(ct);
}
