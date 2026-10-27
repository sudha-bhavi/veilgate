using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Llm;
using VeilGate.Application.LlmProviders;
using VeilGate.Domain.LlmProviders;

namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// The current tenant's providers from the database, with their keys decrypted just in time. There is no tenant parameter on purpose:
/// row-level security limits every read to the tenant of the request, so a provider of another tenant cannot be reached from here.
/// A provider whose key cannot be decrypted (tampered row, lost key version) is skipped and logged rather than failing the whole request.
/// </summary>
public sealed class LlmProviderCatalog(
    ILlmProviderRepository providers, ITenantSecretProtector protector, ILogger<LlmProviderCatalog> logger) : ILlmProviderCatalog
{
    public async Task<IReadOnlyList<LlmProviderConfig>> GetForModelAsync(string model, CancellationToken ct = default)
    {
        var result = new List<LlmProviderConfig>();
        foreach (var provider in (await providers.ListAsync(ct)).Where(p => p.Enabled && p.Serves(model)))
        {
            var config = await ToConfigAsync(provider, ct);
            if (config is not null) result.Add(config);
        }
        return result;
    }

    public async Task<bool> ServesModelAsync(string model, CancellationToken ct = default) =>
        (await providers.ListAsync(ct)).Any(p => p.Enabled && p.Serves(model));

    public async Task<LlmProviderConfig?> FindAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await providers.FindAsync(providerId, ct);
        return provider is null ? null : await ToConfigAsync(provider, ct);
    }

    private async Task<LlmProviderConfig?> ToConfigAsync(LlmProvider p, CancellationToken ct)
    {
        try
        {
            var key = await protector.UnprotectAsync(p.TenantId, p.ApiKeyCipher, p.DekVersion, LlmProviderKeys.Context(p.TenantId, p.Id), ct);
            return new LlmProviderConfig(p.Id, p.TenantId, p.Name, p.BaseUrl, key, p.TimeoutSeconds, p.Priority, p.Models);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            logger.LogError("The API key of LLM provider {Provider} could not be decrypted; the provider is skipped", p.Name);
            return null;
        }
    }
}
