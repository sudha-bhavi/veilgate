using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Api.Security;
using VeilGate.Application.Auth;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// First-time MFA setup. Requires the short-lived token from the password step (not an access token).
/// The user and tenant come from that signed token, never from the request body.
/// </summary>
[ApiController]
[Route("api/v1/auth/mfa/totp")]
[Authorize(AuthenticationSchemes = TokenService.MfaChallengeScheme)]
public sealed class MfaController(StartTotpEnrollmentHandler start, ConfirmTotpHandler confirm) : ApiControllerBase
{
    public sealed record ConfirmRequest(string Code);

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var tenantId)) return Fail(InvalidToken);

        var result = await start.HandleAsync(new StartTotpEnrollmentCommand(userId, tenantId), ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new { otpAuthUri = result.Value.OtpAuthUri, manualEntryKey = result.Value.ManualEntryKey });
    }

    [HttpPost("confirm")]
    [EnableRateLimiting("login")]   // guessing 6-digit codes is a brute-force target, so this is limited per IP
    public async Task<IActionResult> Confirm([FromBody] ConfirmRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var tenantId)) return Fail(InvalidToken);

        var result = await confirm.HandleAsync(new ConfirmTotpCommand(userId, tenantId, request.Code), ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new { mfaConfirmed = true });
    }

    private static readonly Error InvalidToken = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    private bool TryGetIdentity(out Guid userId, out Guid tenantId)
    {
        var ok = Guid.TryParse(User.FindFirst("sub")?.Value, out userId)
               & Guid.TryParse(User.FindFirst("tid")?.Value, out tenantId);
        return ok && userId != Guid.Empty && tenantId != Guid.Empty;
    }
}