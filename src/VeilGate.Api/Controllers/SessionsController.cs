using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// "My sessions" (Backend doc 3.2): every signed-in person, whatever their role, may see and end their own sessions, and only their own.
/// The session in use is the token's "sid". A demo session can look but not change anything: the demo guard refuses the DELETE and
/// POST before they get here.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[Authorize]
public sealed class SessionsController(IClock clock) : ApiControllerBase
{
    private static readonly Error NotAPerson = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    /// <param name="KeepCurrent">Leave out to keep the session in use (the default).</param>
    public sealed record LogoutAllRequest(bool? KeepCurrent);

    [HttpGet("sessions")]
    public async Task<IActionResult> List([FromServices] ListMySessionsHandler handler, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out _, out var sessionId)) return Fail(NotAPerson);
        return Ok(await handler.HandleAsync(userId, sessionId, ct));
    }

    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, [FromServices] RevokeMySessionHandler handler, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var tenantId, out var sessionId)) return Fail(NotAPerson);

        var result = await handler.HandleAsync(new RevokeMySessionCommand(tenantId, userId, sessionId, id, CallerIp), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }

    /// <summary>
    /// Signs the person out of every other device (keepCurrent true, the default) or all of them. Needs a recent second-factor
    /// check. Ending the session in use also drops its refresh cookie.
    /// </summary>
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(
        [FromServices] LogoutAllHandler handler, CancellationToken ct, [FromBody] LogoutAllRequest? request = null)
    {
        if (!TryGetIdentity(out var userId, out var tenantId, out var sessionId)) return Fail(NotAPerson);
        if (RequireRecentMfa(clock.UtcNow) is { } stepUp) return stepUp;

        var keepCurrent = request?.KeepCurrent ?? true;
        var result = await handler.HandleAsync(new LogoutAllCommand(tenantId, userId, sessionId, keepCurrent, CallerIp), ct);
        if (result.IsFailure) return Fail(result.Error);

        if (!keepCurrent) RefreshCookie.Clear(Response);
        return Ok(new { ended = result.Value });
    }

    // A client token (an API client, not a person) has no "sid" and so cannot have sessions.
    private bool TryGetIdentity(out Guid userId, out Guid tenantId, out Guid sessionId)
    {
        userId = tenantId = sessionId = Guid.Empty;
        return Guid.TryParse(User.FindFirst("sub")?.Value, out userId)
            && Guid.TryParse(User.FindFirst("tid")?.Value, out tenantId)
            && Guid.TryParse(User.FindFirst("sid")?.Value, out sessionId);
    }
}
