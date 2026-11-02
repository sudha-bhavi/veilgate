using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Api.Users;
using VeilGate.Application.Users;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The two endpoints behind the emailed link. Nobody is signed in here: the secret token in the link is the only proof.
/// Both are limited per address like sign-in (accepting hashes a password, which costs real CPU). A link that is unknown, used,
/// expired or replaced always gets the same answer.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1")]
public sealed class InvitationsController : ApiControllerBase
{
    /// <summary>
    /// Shows who invited you before you fill anything in. The token travels in the query string, not the path, because the
    /// request log records the path and a token must never be written to a log.
    /// </summary>
    [HttpGet("auth/invitations")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> View([FromQuery] string? token, [FromServices] ViewInvitationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(token, ct);
        if (result.IsFailure) return Fail(result.Error);

        Response.Headers.CacheControl = "no-store";
        return Ok(result.Value);
    }

    /// <summary>
    /// Sets the name and password and uses up the link. The reply has the same shape as the password step of sign-in, so the console
    /// carries on into two-step setup exactly as it does for a first sign-in.
    /// </summary>
    [HttpPost("users/accept-invite")]
    [EnableRateLimiting("login")]
    [Consumes("application/json")]
    public async Task<IActionResult> Accept([FromBody] AcceptInvitationRequest request, [FromServices] AcceptInvitationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AcceptInvitationCommand(request.Token, request.FullName, request.Password, CallerIp), ct);
        if (result.IsFailure) return Fail(result.Error);

        Response.Headers.CacheControl = "no-store";
        return Ok(new { mfaRequired = false, mfaEnrollmentRequired = true, mfaToken = result.Value.MfaToken });
    }
}
