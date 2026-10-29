using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Audit;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The tenant's audit log, read-only (Database doc 4.1). There is no way to change or delete an event here, and the database
/// refuses it too (triggers on audit_events). Another tenant's events are never visible: row-level security limits every query.
/// </summary>
[ApiController]
[Route("api/v1/audit-events")]
[Authorize(Policy = AuthPolicies.AuditReader)]
public sealed class AuditEventsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] ListAuditEventsHandler handler, CancellationToken ct,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? action = null, [FromQuery] string? actorType = null, [FromQuery] Guid? actorId = null,
        [FromQuery] string? resourceType = null, [FromQuery] Guid? resourceId = null, [FromQuery] string? result = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var filter = new AuditEventFilter(from, to, action, actorType, actorId, resourceType, resourceId, result);
        var listed = await handler.HandleAsync(filter, page, pageSize, ct);
        return listed.IsFailure ? Fail(listed.Error) : Ok(listed.Value);
    }

    [HttpGet("actions")]
    public async Task<IActionResult> Actions([FromServices] ListAuditActionsHandler handler, CancellationToken ct) =>
        Ok(new { items = await handler.HandleAsync(ct) });

    /// <summary>Recomputes every hash of the tenant's chain. It reads the whole log, so the console runs it only when asked.</summary>
    [HttpGet("verify")]
    public async Task<IActionResult> Verify([FromServices] VerifyAuditChainHandler handler, CancellationToken ct) =>
        Ok(await handler.HandleAsync(ct));
}
