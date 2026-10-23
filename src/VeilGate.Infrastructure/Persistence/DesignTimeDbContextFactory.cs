using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VeilGate.Infrastructure.Persistence;

/// <summary>
/// Used only by "dotnet ef" so it can build the context without starting the API.
/// Migrations run as vg_migrator (the schema owner). Override with the VEILGATE_MIGRATOR_CONNECTION environment variable.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VeilGateDbContext>
{
    public VeilGateDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("VEILGATE_MIGRATOR_CONNECTION")
                 ?? "Host=localhost;Port=5432;Database=veilgate;Username=vg_migrator;Password=change-me-migrator";
        var options = new DbContextOptionsBuilder<VeilGateDbContext>().UseNpgsql(cs).Options;
        return new VeilGateDbContext(options);
    }
}
