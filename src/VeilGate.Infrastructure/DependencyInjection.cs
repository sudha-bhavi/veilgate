using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Infrastructure.Persistence;

namespace VeilGate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddDbContext<VeilGateDbContext>((sp, o) =>
            o.UseNpgsql(cs, npg => npg.MigrationsAssembly(typeof(VeilGateDbContext).Assembly.FullName))
             .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        return services;
    }
}

internal sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
