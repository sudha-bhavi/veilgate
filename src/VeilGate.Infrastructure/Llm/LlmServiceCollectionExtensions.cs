using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Llm;

namespace VeilGate.Infrastructure.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gateway that sends sanitized chat requests to the tenant's providers (Backend doc 6.5). Which providers exist is
    /// not known at startup any more: they are read per request from the database, and their clients are built and kept on first use.
    /// </summary>
    public static IServiceCollection AddLlmGateway(this IServiceCollection services, LlmOptions options)
    {
        services.AddSingleton<IOptions<LlmOptions>>(Options.Create(options));
        services.AddSingleton(sp => new ProviderClientCache(sp.GetRequiredService<IOptions<LlmOptions>>()));
        services.AddSingleton<ProviderCaller>();
        services.AddSingleton<IOutboundUrlValidator>(sp => new OutboundUrlValidator(sp.GetRequiredService<IOptions<LlmOptions>>()));
        services.AddSingleton<ILlmProviderTester, LlmProviderTester>();
        services.AddScoped<ILlmProviderCatalog, LlmProviderCatalog>();
        services.AddScoped<ILlmGateway, LlmGateway>();
        return services;
    }
}
