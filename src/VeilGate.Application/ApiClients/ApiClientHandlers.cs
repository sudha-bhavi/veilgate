using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.ApiClients;
using VeilGate.Domain.Common;

namespace VeilGate.Application.ApiClients;

// Managing API clients (Backend doc 5.1). Tenant Admins manage every client of their tenant; Developers only their own.
// A client someone may not see is reported as "not found", the same as one that does not exist. Secrets are returned once
// (on create and on rotate) and are stored only as Argon2id hashes. The recent-MFA proof for create and rotate is checked by the controller.

public sealed record CreateApiClientCommand(
    string Name, string[] Scopes, Guid? DefaultPolicyId, string[]? IpAllowlist, int? RateLimitPerMinute, ApiClientActor Actor);

/// <summary>The only moment the plaintext secret exists outside the caller's own storage.</summary>
public sealed record CreatedApiClient(ApiClientView Client, string ClientSecret);

public sealed class CreateApiClientHandler(
    IApiClientRepository clients, IPolicyRepository policies, IPasswordHasher hasher, ITenantContext tenant, ApiClientAuditor auditor)
{
    public async Task<Result<CreatedApiClient>> HandleAsync(CreateApiClientCommand cmd, CancellationToken ct = default)
    {
        var client = ApiClient.Create(tenant.TenantId, cmd.Actor.UserId, cmd.Name, cmd.Scopes, cmd.DefaultPolicyId, cmd.IpAllowlist, cmd.RateLimitPerMinute);
        if (client.IsFailure) return client.Error;

        if (await clients.NameExistsAsync(client.Value.Name, ct))
            return Error.Conflict("resource.conflict", "A client with this name already exists.");

        if (cmd.DefaultPolicyId is { } policyId)
        {
            var policy = await policies.FindAsync(policyId, ct);
            if (policy is null || policy.IsArchived) return Error.Validation("validation.failed", "Default policy not found.");
        }

        var secret = ApiClientCredentials.NewSecret();
        var stored = ApiClientSecret.Create(tenant.TenantId, client.Value.Id, hasher.Hash(secret));
        if (stored.IsFailure) return stored.Error;

        clients.Add(client.Value);
        clients.AddSecret(stored.Value);
        await clients.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "apiclient.create", client.Value, ct);
        return new CreatedApiClient(ApiClientView.From(client.Value), secret);
    }
}

public sealed record UpdateApiClientCommand(
    Guid Id, string[]? Scopes, string[]? IpAllowlist, int? RateLimitPerMinute, Guid? DefaultPolicyId, ApiClientActor Actor);

public sealed class UpdateApiClientHandler(IApiClientRepository clients, IPolicyRepository policies, ApiClientAuditor auditor)
{
    public async Task<Result<ApiClientView>> HandleAsync(UpdateApiClientCommand cmd, CancellationToken ct = default)
    {
        var client = await clients.FindAsync(cmd.Id, ct);
        if (client is null || !cmd.Actor.OwnsOrManages(client)) return Error.NotFound();

        if (cmd.DefaultPolicyId is { } policyId)
        {
            var policy = await policies.FindAsync(policyId, ct);
            if (policy is null || policy.IsArchived) return Error.Validation("validation.failed", "Default policy not found.");
        }

        var updated = client.Update(cmd.Scopes, cmd.IpAllowlist, cmd.RateLimitPerMinute, cmd.DefaultPolicyId);
        if (updated.IsFailure) return updated.Error;

        await clients.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "apiclient.update", client, ct);
        return ApiClientView.From(client);
    }
}

public sealed record RotateSecretCommand(Guid Id, int GraceHours, ApiClientActor Actor);
public sealed record RotatedSecret(string ClientSecret, DateTimeOffset OldSecretsEndAt);

/// <summary>
/// Issues a new secret and lets the old one keep working for a grace period, so a client can be reconfigured without downtime.
/// At most two secrets work at once; rotating again before the first grace period ends is refused.
/// </summary>
public sealed class RotateSecretHandler(
    IApiClientRepository clients, IPasswordHasher hasher, ITenantContext tenant, IClock clock, ApiClientAuditor auditor)
{
    public static readonly int[] AllowedGraceHours = [1, 24, 168];

    public async Task<Result<RotatedSecret>> HandleAsync(RotateSecretCommand cmd, CancellationToken ct = default)
    {
        if (!AllowedGraceHours.Contains(cmd.GraceHours))
            return Error.Validation("validation.failed", "Grace period must be 1, 24 or 168 hours.");

        var client = await clients.FindAsync(cmd.Id, ct);
        if (client is null || !cmd.Actor.OwnsOrManages(client)) return Error.NotFound();
        if (!client.IsActive) return Error.Conflict("resource.conflict", "A revoked client cannot be changed.");

        var now = clock.UtcNow;
        var current = (await clients.ListSecretsAsync(client.Id, ct)).Where(s => s.IsValidAt(now)).ToList();
        if (current.Count >= 2)
            return Error.Conflict("apiclient.rotation_in_progress", "A previous rotation is still in its grace period.");

        var endAt = now.AddHours(cmd.GraceHours);
        foreach (var old in current) old.ExpireNoLaterThan(endAt);

        var secret = ApiClientCredentials.NewSecret();
        var stored = ApiClientSecret.Create(tenant.TenantId, client.Id, hasher.Hash(secret));
        if (stored.IsFailure) return stored.Error;

        clients.AddSecret(stored.Value);
        await clients.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "apiclient.rotate", client, ct);
        return new RotatedSecret(secret, endAt);
    }
}

public sealed record RevokeApiClientCommand(Guid Id, ApiClientActor Actor);

/// <summary>Revokes the client and ends all its secrets at once. The gateway checks the client on every call, so its tokens stop working immediately.</summary>
public sealed class RevokeApiClientHandler(IApiClientRepository clients, IClock clock, ApiClientAuditor auditor)
{
    public async Task<Result> HandleAsync(RevokeApiClientCommand cmd, CancellationToken ct = default)
    {
        var client = await clients.FindAsync(cmd.Id, ct);
        if (client is null || !cmd.Actor.OwnsOrManages(client)) return Result.Failure(Error.NotFound());

        var now = clock.UtcNow;
        var revoked = client.Revoke(now);
        if (revoked.IsFailure) return revoked;

        foreach (var secret in await clients.ListSecretsAsync(client.Id, ct)) secret.ExpireNoLaterThan(now);

        await clients.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "apiclient.revoke", client, ct);
        return Result.Success();
    }
}

public sealed class GetApiClientHandler(IApiClientRepository clients)
{
    public async Task<Result<ApiClientView>> HandleAsync(Guid id, ApiClientActor actor, CancellationToken ct = default)
    {
        var client = await clients.FindAsync(id, ct);
        if (client is null || !actor.OwnsOrManages(client)) return Error.NotFound();
        return ApiClientView.From(client);
    }
}

public sealed class ListApiClientsHandler(IApiClientRepository clients)
{
    public async Task<Result<Paged<ApiClientView>>> HandleAsync(
        ApiClientActor actor, string? status, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        if (status is not null && !Enum.TryParse<ApiClientStatus>(status, out _))
            return Error.Validation("validation.failed", "Unknown status.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var owner = actor.Role == ApiClientActor.DeveloperRole ? actor.UserId : (Guid?)null;
        var (items, total) = await clients.ListAsync(owner, status, search, page, pageSize, ct);
        return new Paged<ApiClientView>(items.Select(ApiClientView.From).ToList(), page, pageSize, total);
    }
}
