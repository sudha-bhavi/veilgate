using VeilGate.Application.Abstractions;
using VeilGate.Application.Llm;
using VeilGate.Domain.Common;
using VeilGate.Domain.LlmProviders;

namespace VeilGate.Application.LlmProviders;

// Managing a tenant's language model providers (Backend doc 5.1). Tenant Admins only; the controller enforces the role.
// An API key goes in once and is stored encrypted under the tenant's own data key; nothing ever returns it.

public sealed record CreateLlmProviderCommand(
    string Name, string BaseUrl, string ApiKey, string[] Models, int? TimeoutSeconds, int? Priority, bool? Enabled, LlmProviderActor Actor);

public sealed class CreateLlmProviderHandler(
    ILlmProviderRepository providers, ITenantSecretProtector protector, IOutboundUrlValidator urls,
    ITenantContext tenant, LlmProviderAuditor auditor)
{
    public async Task<Result<LlmProviderView>> HandleAsync(CreateLlmProviderCommand cmd, CancellationToken ct = default)
    {
        // Cheap checks first; the address check can involve a DNS lookup.
        if (!LlmProvider.TryNormalizeBaseUrl(cmd.BaseUrl, out var url)) return Error.Validation("validation.failed", "Invalid provider URL.");
        if (!LlmProviderKeys.IsAcceptable(cmd.ApiKey))
            return Error.Validation("validation.failed", $"API key must be {LlmProviderKeys.MinKeyLength} to {LlmProviderKeys.MaxKeyLength} characters.");

        var name = (cmd.Name ?? string.Empty).Trim();
        if (await providers.NameExistsAsync(name, null, ct)) return Error.Conflict("resource.conflict", "A provider with this name already exists.");

        var urlCheck = await urls.ValidateAsync(url, ct);
        if (urlCheck.IsFailure) return urlCheck.Error;

        var existing = await providers.ListAsync(ct);
        var priority = cmd.Priority ?? (existing.Count == 0 ? 1 : existing.Max(p => p.Priority) + 1);

        // The encrypted key is bound to the provider's id, so the id has to exist before the key is encrypted.
        var id = Guid.CreateVersion7();
        var secret = await protector.ProtectAsync(tenant.TenantId, cmd.ApiKey, LlmProviderKeys.Context(tenant.TenantId, id), ct);
        var key = new ProtectedApiKey(secret.Cipher, secret.DekVersion, LlmProvider.Last4Of(cmd.ApiKey));

        var provider = LlmProvider.Create(id, tenant.TenantId, name, url, cmd.Models, key, cmd.TimeoutSeconds, priority, cmd.Enabled ?? true);
        if (provider.IsFailure) return provider.Error;

        providers.Add(provider.Value);
        await providers.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "llmprovider.create", provider.Value.Id, provider.Value.Name, ct);
        return LlmProviderView.From(provider.Value);
    }
}

/// <param name="ApiKey">Null keeps the stored key. Required when the URL changes: see <see cref="UpdateLlmProviderHandler"/>.</param>
public sealed record UpdateLlmProviderCommand(
    Guid Id, string Name, string BaseUrl, string? ApiKey, string[] Models, int? TimeoutSeconds, int? Priority, bool? Enabled, LlmProviderActor Actor);

public sealed class UpdateLlmProviderHandler(
    ILlmProviderRepository providers, ITenantSecretProtector protector, IOutboundUrlValidator urls,
    ITenantContext tenant, LlmProviderAuditor auditor)
{
    public async Task<Result<LlmProviderView>> HandleAsync(UpdateLlmProviderCommand cmd, CancellationToken ct = default)
    {
        var provider = await providers.FindAsync(cmd.Id, ct);
        if (provider is null) return Error.NotFound();

        if (!LlmProvider.TryNormalizeBaseUrl(cmd.BaseUrl, out var url)) return Error.Validation("validation.failed", "Invalid provider URL.");
        if (cmd.ApiKey is not null && !LlmProviderKeys.IsAcceptable(cmd.ApiKey))
            return Error.Validation("validation.failed", $"API key must be {LlmProviderKeys.MinKeyLength} to {LlmProviderKeys.MaxKeyLength} characters.");

        // The stored key is never shown, so it must never be sent somewhere new on the strength of an edit alone. Pointing an existing
        // provider at a different address would otherwise let an admin (or a stolen admin session) collect the key at a server of their own.
        var urlChanged = !string.Equals(url, provider.BaseUrl, StringComparison.Ordinal);
        if (urlChanged && cmd.ApiKey is null)
            return Error.Validation("provider.key_required", "Enter the API key again when you change the URL.");

        var name = (cmd.Name ?? string.Empty).Trim();
        if (await providers.NameExistsAsync(name, provider.Id, ct)) return Error.Conflict("resource.conflict", "A provider with this name already exists.");

        if (urlChanged)
        {
            var urlCheck = await urls.ValidateAsync(url, ct);
            if (urlCheck.IsFailure) return urlCheck.Error;
        }

        var updated = provider.Update(name, url, cmd.Models, cmd.TimeoutSeconds, cmd.Enabled, cmd.Priority);
        if (updated.IsFailure) return updated.Error;

        if (cmd.ApiKey is not null)
        {
            var secret = await protector.ProtectAsync(tenant.TenantId, cmd.ApiKey, LlmProviderKeys.Context(tenant.TenantId, provider.Id), ct);
            var replaced = provider.ReplaceApiKey(new ProtectedApiKey(secret.Cipher, secret.DekVersion, LlmProvider.Last4Of(cmd.ApiKey)));
            if (replaced.IsFailure) return replaced.Error;
        }

        await providers.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "llmprovider.update", provider.Id, provider.Name, ct);
        return LlmProviderView.From(provider);
    }
}

public sealed record ReorderLlmProvidersCommand(Guid[] Ids, LlmProviderActor Actor);

/// <summary>Sets the failover order. The list must name every provider of the tenant exactly once, so a half-finished list can never leave the order ambiguous.</summary>
public sealed class ReorderLlmProvidersHandler(ILlmProviderRepository providers, LlmProviderAuditor auditor)
{
    public async Task<Result> HandleAsync(ReorderLlmProvidersCommand cmd, CancellationToken ct = default)
    {
        var all = await providers.ListAsync(ct);
        var wanted = cmd.Ids ?? [];
        if (wanted.Length != all.Count || wanted.Distinct().Count() != wanted.Length || !all.Select(p => p.Id).ToHashSet().SetEquals(wanted))
            return Result.Failure(Error.Validation("validation.failed", "List every provider exactly once."));

        var byId = all.ToDictionary(p => p.Id);
        for (var i = 0; i < wanted.Length; i++) byId[wanted[i]].SetPriority(i + 1);

        await providers.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "llmprovider.reorder", null, null, ct);
        return Result.Success();
    }
}

public sealed record DeleteLlmProviderCommand(Guid Id, LlmProviderActor Actor);

public sealed class DeleteLlmProviderHandler(ILlmProviderRepository providers, LlmProviderAuditor auditor)
{
    public async Task<Result> HandleAsync(DeleteLlmProviderCommand cmd, CancellationToken ct = default)
    {
        var provider = await providers.FindAsync(cmd.Id, ct);
        if (provider is null) return Result.Failure(Error.NotFound());

        providers.Remove(provider);
        await providers.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "llmprovider.delete", provider.Id, provider.Name, ct);
        return Result.Success();
    }
}

public sealed record TestLlmProviderCommand(Guid Id, LlmProviderActor Actor);

/// <summary>Calls the provider with a harmless "ping" so an admin can see whether the address, key and model work. Reports a category, never the provider's own error text.</summary>
public sealed class TestLlmProviderHandler(ILlmProviderCatalog catalog, ILlmProviderTester tester, LlmProviderAuditor auditor)
{
    public async Task<Result<ProviderTestResult>> HandleAsync(TestLlmProviderCommand cmd, CancellationToken ct = default)
    {
        var config = await catalog.FindAsync(cmd.Id, ct);
        if (config is null) return Error.NotFound();

        var result = await tester.TestAsync(config, ct);
        await auditor.WriteAsync(cmd.Actor, "llmprovider.test", config.Id, config.Name, ct,
            result.Ok ? "success" : "failure", result.ErrorCategory);
        return result;
    }
}

public sealed class GetLlmProviderHandler(ILlmProviderRepository providers)
{
    public async Task<Result<LlmProviderView>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var provider = await providers.FindAsync(id, ct);
        return provider is null ? Error.NotFound() : LlmProviderView.From(provider);
    }
}

public sealed class ListLlmProvidersHandler(ILlmProviderRepository providers)
{
    public async Task<IReadOnlyList<LlmProviderView>> HandleAsync(CancellationToken ct = default) =>
        (await providers.ListAsync(ct)).Select(LlmProviderView.From).ToList();
}
