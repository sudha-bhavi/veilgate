using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;
using VeilGate.Application.Audit;

namespace VeilGate.Api.Controllers;

/// <summary>
/// Security events, read-only (Backend doc 3.3). Every tenant role may ask, but what comes back depends on the role: Tenant Admins
/// and Security Officers see the whole organization and may filter by person; everyone else sees only events about themselves.
/// The rule is in the handler. Another tenant's events are never visible: row-level security limits every query.
/// </summary>
[ApiController]
[Route("api/v1/security-events")]
[Authorize(Policy = AuthPolicies.SecurityEventReader)]
public sealed class SecurityEventsController : ApiControllerBase
{
    private bool SeesWholeOrganization => User.IsInRole("TenantAdmin") || User.IsInRole("SecurityOfficer");

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] ListSecurityEventsHandler handler, CancellationToken ct,
        [FromQuery] Guid? userId = null, [FromQuery] string? severity = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        if (!Guid.TryParse(User.FindFirst("sub")?.Value, out var callerId)) return Unauthorized();

        var query = new ListSecurityEventsQuery(callerId, SeesWholeOrganization, userId, severity, from, to);
        var listed = await handler.HandleAsync(query, page, pageSize, ct);
        return listed.IsFailure ? Fail(listed.Error) : Ok(listed.Value);
    }

    /// <summary>The people to choose from in the "User" filter. Only for those who may filter by person at all.</summary>
    [HttpGet("people")]
    [Authorize(Policy = AuthPolicies.SecurityOversight)]
    public async Task<IActionResult> People([FromServices] ListSecurityEventPeopleHandler handler, CancellationToken ct) =>
        Ok(new { items = await handler.HandleAsync(ct) });
}
