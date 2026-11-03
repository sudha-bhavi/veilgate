using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// "My account" security (Backend doc 3.2): what the signed-in person has set up for two-step sign-in, new recovery codes, and a new
/// password. Every role, always about the caller themselves. A demo session cannot change anything: the demo guard refuses the POSTs.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[Authorize]
public sealed class AccountSecurityController(IClock clock) : ApiControllerBase
{
    private static readonly Error NotAPerson = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    [HttpGet("mfa/factors")]
    public async Task<IActionResult> Factors([FromServices] GetMfaStatusHandler handler, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out _)) return Fail(NotAPerson);

        var status = await handler.HandleAsync(userId, ct);
        return Ok(new
        {
            authenticator = status.AddedAt is { } added ? new { addedAt = added } : null,
            recoveryCodesLeft = status.RecoveryCodesLeft,
            recoveryCodesTotal = status.RecoveryCodesTotal
        });
    }

    /// <summary>Replaces all recovery codes. Needs a recent second-factor check. The new codes are in this answer only.</summary>
    [HttpPost("mfa/recovery-codes/regenerate")]
    public async Task<IActionResult> RegenerateRecoveryCodes([FromServices] RegenerateRecoveryCodesHandler handler, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out _)) return Fail(NotAPerson);
        if (RequireRecentMfa(clock.UtcNow) is { } stepUp) return stepUp;

        var result = await handler.HandleAsync(new RegenerateRecoveryCodesCommand(userId, CallerIp), ct);
        if (result.IsFailure) return Fail(result.Error);

        Response.Headers.CacheControl = "no-store";   // secrets: never kept by a cache
        return Ok(new { recoveryCodes = result.Value });
    }

    /// <summary>Limited per address like sign-in, because a wrong current password counts toward the account lockout.</summary>
    [HttpPost("password/change")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request, [FromServices] ChangePasswordHandler handler, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var sessionId)) return Fail(NotAPerson);

        var result = await handler.HandleAsync(
            new ChangePasswordCommand(userId, sessionId, request.CurrentPassword, request.NewPassword, CallerIp), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }

    // A client token (an API client, not a person) has no "sid", so it cannot use these.
    private bool TryGetIdentity(out Guid userId, out Guid sessionId)
    {
        userId = sessionId = Guid.Empty;
        return Guid.TryParse(User.FindFirst("sub")?.Value, out userId) && Guid.TryParse(User.FindFirst("sid")?.Value, out sessionId);
    }
}
