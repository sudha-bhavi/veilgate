using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Api.LlmProviders;
using VeilGate.Api.Security;
using VeilGate.Application.LlmProviders;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The language model providers a tenant has connected (Backend doc 5.1). Tenant Admins only. The API key is accepted on create and
/// update and is never returned: a provider shows only the last four characters of its key. Where a provider lives is checked when it is
/// saved (https, and not an internal network) and again on every connection. A provider of another tenant is simply "not found".
/// </summary>
[ApiController]
[Route("api/v1/llm-providers")]
[Authorize(Policy = AuthPolicies.LlmProviderAdmin)]
public sealed class LlmProvidersController : ApiControllerBase
{
    private LlmProviderActor Actor => new(Guid.Parse(User.FindFirst("sub")!.Value), HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpGet]
    public async Task<IActionResult> List([FromServices] ListLlmProvidersHandler handler, CancellationToken ct) =>
        Ok(new { items = await handler.HandleAsync(ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] GetLlmProviderHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Create([FromBody] CreateLlmProviderRequest request, [FromServices] CreateLlmProviderHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreateLlmProviderCommand(
            request.Name, request.BaseUrl, request.ApiKey, request.Models, request.TimeoutSeconds, request.Priority, request.Enabled, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Created($"/api/v1/llm-providers/{result.Value.Id}", result.Value);
    }

    [HttpPut("{id:guid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLlmProviderRequest request, [FromServices] UpdateLlmProviderHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UpdateLlmProviderCommand(
            id, request.Name, request.BaseUrl, request.ApiKey, request.Models, request.TimeoutSeconds, request.Priority, request.Enabled, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    /// <summary>The failover order: first in the list is tried first.</summary>
    [HttpPut("order")]
    [Consumes("application/json")]
    public async Task<IActionResult> Reorder([FromBody] ReorderLlmProvidersRequest request, [FromServices] ReorderLlmProvidersHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ReorderLlmProvidersCommand(request.Ids, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }

    /// <summary>Makes a real call to the provider, so it is limited like gateway traffic.</summary>
    [HttpPost("{id:guid}/test")]
    [EnableRateLimiting("gateway")]
    public async Task<IActionResult> Test(Guid id, [FromServices] TestLlmProviderHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new TestLlmProviderCommand(id, Actor), ct);
        if (result.IsFailure) return Fail(result.Error);
        return Ok(new { ok = result.Value.Ok, latencyMs = result.Value.LatencyMs, errorCategory = result.Value.ErrorCategory });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromServices] DeleteLlmProviderHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeleteLlmProviderCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }
}
