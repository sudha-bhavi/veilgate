using VeilGate.Domain.ApiClients;

namespace VeilGate.Application.Abstractions;

/// <summary>What the gateway needs to know about a client on every request: still active, where it may call from, which policy it defaults to.</summary>
public sealed record ApiClientState(ApiClientStatus Status, string[] IpAllowlist, Guid? DefaultPolicyId);

public interface IApiClientRepository
{
    void Add(ApiClient client);
    void AddSecret(ApiClientSecret secret);

    /// <summary>
    /// The one lookup that runs before the tenant is known (the token request carries only the client id).
    /// It returns just the tenant id; everything after it is read with row-level security on.
    /// </summary>
    Task<Guid?> FindTenantByClientIdAsync(string clientId, CancellationToken ct);

    /// <summary>Tenant-scoped, by public client id (vg_...). Null if it does not exist in the current tenant.</summary>
    Task<ApiClient?> FindByClientIdAsync(string clientId, CancellationToken ct);

    /// <summary>Tenant-scoped, by database id. Tracked, so the caller can change it and save.</summary>
    Task<ApiClient?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>A light, untracked read for the hot path (every gateway call). Null if there is no such client in the current tenant.</summary>
    Task<ApiClientState?> GetStateAsync(Guid id, CancellationToken ct);

    /// <summary>All secrets of the client, including expired ones, tracked so a rotation can end them.</summary>
    Task<IReadOnlyList<ApiClientSecret>> ListSecretsAsync(Guid clientPk, CancellationToken ct);

    Task<bool> NameExistsAsync(string name, CancellationToken ct);

    /// <summary>One page, by name. <paramref name="ownerUserId"/> limits the list to that person's clients (a Developer sees only their own).</summary>
    Task<(IReadOnlyList<ApiClient> Items, int Total)> ListAsync(
        Guid? ownerUserId, string? status, string? search, int page, int pageSize, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
