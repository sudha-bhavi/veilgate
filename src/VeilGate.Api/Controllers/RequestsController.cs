using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Requests;

namespace VeilGate.Api.Controllers;

/// <summary>
/// What went through the gateway, read-only. Each row holds counts and names, never the text. The rows are written by the metrics
/// consumer from the gateway events, so a call shows up here a moment after it was made. Another tenant's requests are never visible.
/// </summary>
[ApiController]
[Route("api/v1/requests")]
[Authorize(Policy = AuthPolicies.RequestReader)]
public sealed class RequestsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] ListGatewayRequestsHandler handler, CancellationToken ct,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? action = null, [FromQuery] string? status = null, [FromQuery] Guid? clientId = null,
        [FromQuery] string? requestId = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var filter = new GatewayRequestFilter(from, to, action, status, clientId, requestId);
        var listed = await handler.HandleAsync(filter, page, pageSize, ct);
        return listed.IsFailure ? Fail(listed.Error) : Ok(listed.Value);
    }

    [HttpGet("actions")]
    public async Task<IActionResult> Actions([FromServices] ListRequestActionsHandler handler, CancellationToken ct) =>
        Ok(new { items = await handler.HandleAsync(ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] GetGatewayRequestHandler handler, CancellationToken ct)
    {
        var found = await handler.HandleAsync(id, ct);
        return found.IsFailure ? Fail(found.Error) : Ok(found.Value);
    }
}
