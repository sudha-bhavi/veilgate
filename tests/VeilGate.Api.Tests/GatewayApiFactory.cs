using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Llm;
using VeilGate.Domain.Common;
using VeilGate.TestSupport;

namespace VeilGate.Api.Tests;

/// <summary>
/// The real API on the throwaway Postgres (real vault, real audit log, real row-level security), with one thing replaced:
/// the language model. Tests decide what the "model" answers and can see exactly what it was sent.
/// </summary>
public sealed class GatewayApiFactory : WebApplicationFactory<Program>
{
    public const string Kid = "test-key-1";
    private readonly PostgresFixture _pg;
    private readonly string _privatePem;
    private readonly int _rateLimit;

    /// <summary>Every request the "model" received, as the sanitized text it was given.</summary>
    public List<LlmRequest> ModelRequests { get; } = [];

    /// <summary>What the "model" answers. Tests replace this to script a reply or a failure.</summary>
    public Func<LlmRequest, Result<LlmResponse>> ModelReply { get; set; } =
        _ => Result.Success(new LlmResponse("chatcmpl-test", "gpt-4o-mini", "Mock reply", "stop", 11, 7, "test-llm"));

    public GatewayApiFactory(PostgresFixture pg, int rateLimitPerMinute = 100_000)
    {
        _pg = pg;
        _rateLimit = rateLimitPerMinute;
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
        builder.UseSetting("Gateway:RateLimitPerClientPerMinute", _rateLimit.ToString());
        builder.UseSetting("Security:MfaEncryptionKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("Security:VaultKek", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        // A provider has to be configured so that "gpt-4o-mini" passes the model check. It is never called: the gateway below replaces the client.
        builder.UseSetting("Llm:AllowPrivateNetworks", "true");
        builder.UseSetting("Llm:Providers:0:Name", "test-llm");
        builder.UseSetting("Llm:Providers:0:BaseUrl", "http://test-llm.invalid");
        builder.UseSetting("Llm:Providers:0:Models:0", "gpt-4o-mini");

        builder.ConfigureTestServices(services => services.AddSingleton<ILlmGateway>(new ScriptedModel(this)));
    }

    private sealed class ScriptedModel(GatewayApiFactory factory) : ILlmGateway
    {
        public Task<Result<LlmResponse>> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            lock (factory.ModelRequests) factory.ModelRequests.Add(request);
            return Task.FromResult(factory.ModelReply(request));
        }
    }
}
