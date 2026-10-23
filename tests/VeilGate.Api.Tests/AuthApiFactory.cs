using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VeilGate.TestSupport;

namespace VeilGate.Api.Tests;

/// <summary>Runs the real API against the throwaway Postgres, with a fresh ES256 signing key and a generous login rate limit.</summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    public const string Kid = "test-key-1";
    private readonly PostgresFixture _pg;
    private readonly string _privatePem;

    public AuthApiFactory(PostgresFixture pg)
    {
        _pg = pg;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _privatePem = key.ExportECPrivateKeyPem();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Postgres", _pg.AppConnectionForApi);
        builder.UseSetting("Auth:Keys:0:Kid", Kid);
        builder.UseSetting("Auth:Keys:0:PrivateKeyPem", _privatePem);
        builder.UseSetting("Auth:LoginRateLimitPerMinute", "1000");
    }
}
