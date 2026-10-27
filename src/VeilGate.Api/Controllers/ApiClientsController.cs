using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.ApiClients;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.Application.ApiClients;

namespace VeilGate.Api.Controllers;

/// <summary>
/// Managing API clients (Backend doc 5.1). Tenant Admins manage every client of their tenant, Developers only their own.
/// Creating a client and rotating a secret hand out credentials, so they need a recent second-factor check. The secret is
/// shown once, in that response, and is never returned again. A client of another tenant is simply "not found".
/// </summary>
[ApiController]
[Route("api/v1/api-clients")]
[Authorize(Policy = AuthPolicies.ApiClientManager)]
public sealed class ApiClientsController(IClock clock) : ApiControllerBase
{
    private ApiClientActor Actor => new(
        Guid.Parse(User.FindFirst("sub")!.Value),
        User.FindFirst("role")?.Value ?? string.Empty,
        HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] ListApiClientsHandler handler, CancellationToken ct,
        [FromQuery] string? status = null, [FromQuery] string? q = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var result = await handler.HandleAsync(Actor, status, q, page, pageSize, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] GetApiClientHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, Actor, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Create([FromBody] CreateApiClientRequest request, [FromServices] CreateApiClientHandler handler, CancellationToken ct)
    {
        if (RequireRecentMfa(clock.UtcNow) is { } stepUp) return stepUp;

        var command = new CreateApiClientCommand(
            request.Name, request.Scopes, request.DefaultPolicyId, request.IpAllowlist, request.RateLimitPerMinute, Actor);
        var result = await handler.HandleAsync(command, ct);
        if (result.IsFailure) return Fail(result.Error);

        var c = result.Value.Client;
        Response.Headers.CacheControl = "no-store";
        return Created($"/api/v1/api-clients/{c.Id}", new
        {
            id = c.Id,
            clientId = c.ClientId,
            clientSecret = result.Value.ClientSecret,   // shown this once
            name = c.Name,
            scopes = c.Scopes,
            status = c.Status,
            defaultPolicyId = c.DefaultPolicyId,
            ipAllowlist = c.IpAllowlist,
            rateLimitPerMinute = c.RateLimitPerMinute
        });
    }

    [HttpPatch("{id:guid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateApiClientRequest request, [FromServices] UpdateApiClientHandler handler, CancellationToken ct)
    {
        var command = new UpdateApiClientCommand(id, request.Scopes, request.IpAllowlist, request.RateLimitPerMinute, request.DefaultPolicyId, Actor);
        var result = await handler.HandleAsync(command, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpPost("{id:guid}/rotate-secret")]
    [Consumes("application/json")]
    public async Task<IActionResult> RotateSecret(Guid id, [FromBody] RotateSecretRequest request, [FromServices] RotateSecretHandler handler, CancellationToken ct)
    {
        if (RequireRecentMfa(clock.UtcNow) is { } stepUp) return stepUp;

        var result = await handler.HandleAsync(new RotateSecretCommand(id, request.GraceHours, Actor), ct);
        if (result.IsFailure) return Fail(result.Error);

        Response.Headers.CacheControl = "no-store";
        return Ok(new { clientSecret = result.Value.ClientSecret, previousSecretsEndAt = result.Value.OldSecretsEndAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, [FromServices] RevokeApiClientHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RevokeApiClientCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }
}
