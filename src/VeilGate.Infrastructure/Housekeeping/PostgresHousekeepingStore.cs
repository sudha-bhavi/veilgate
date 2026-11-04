using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure.Housekeeping;

/// <summary>Calls the two housekeeping functions (see CreateHousekeepingFunctions). No tenant is set: the cleanup serves every tenant.</summary>
public sealed class PostgresHousekeepingStore(VeilGateDbContext db) : IHousekeepingStore
{
    public async Task<int> PurgeExpiredVaultAsync(CancellationToken ct = default) =>
        (await db.Database.SqlQuery<int>($"SELECT vault_purge_expired() AS \"Value\"").ToListAsync(ct)).Single();

    public async Task<int> ReapSessionsAsync(TimeSpan idleTimeout, TimeSpan grace, CancellationToken ct = default)
    {
        var idle = (int)idleTimeout.TotalSeconds;
        var graceSeconds = (int)grace.TotalSeconds;
        return (await db.Database.SqlQuery<int>($"SELECT sessions_reap({idle}, {graceSeconds}) AS \"Value\"").ToListAsync(ct)).Single();
    }
}
