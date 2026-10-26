using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;

namespace VeilGate.Infrastructure;

public static class DependencyInjection
{
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IMfaFactorRepository, MfaFactorRepository>();
        services.AddScoped<IRecoveryCodeRepository, RecoveryCodeRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddSingleton<ISecretProtector>(sp =>
        {
            var base64 = sp.GetRequiredService<IConfiguration>()["Security:MfaEncryptionKey"];
            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException("Security:MfaEncryptionKey is required (32 random bytes, base64-encoded).");
            return new AesGcmSecretProtector(Convert.FromBase64String(base64));
        });
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        // The connection string is read when the first DbContext is built, so configuration overrides (tests, environment variables) always apply.
        services.AddDbContext<VeilGateDbContext>((sp, o) =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
            o.UseNpgsql(cs, npg => npg.MigrationsAssembly(typeof(VeilGateDbContext).Assembly.FullName))
             .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>());
        });
        return services;
    }
}

internal sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
