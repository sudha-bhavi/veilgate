using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VeilGate.Api.Options;
using VeilGate.Api.Security;
using VeilGate.Application.Auth;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The MFA step of sign-in and first-time setup. Every endpoint requires the short-lived token from the password step
/// (not an access token). The user and tenant come from that signed token, never from the request body.
/// </summary>
[ApiController]
[Route("api/v1/auth/mfa")]
[Authorize(AuthenticationSchemes = TokenService.MfaChallengeScheme)]
public sealed class MfaController(
    StartTotpEnrollmentHandler start, ConfirmTotpHandler confirm, VerifyMfaHandler verify,
    IOptions<AuthOptions> auth) : ApiControllerBase
{
    public sealed record CodeRequest(string Code);

    [HttpPost("totp/start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var tenantId, out _)) return Fail(InvalidToken);

        var result = await start.HandleAsync(new StartTotpEnrollmentCommand(userId, tenantId), ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new { otpAuthUri = result.Value.OtpAuthUri, manualEntryKey = result.Value.ManualEntryKey });
    }

    [HttpPost("totp/confirm")]
    [EnableRateLimiting("login")]   // guessing 6-digit codes is a brute-force target, so this is limited per IP
    public async Task<IActionResult> Confirm([FromBody] CodeRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var tenantId, out _)) return Fail(InvalidToken);

        var result = await confirm.HandleAsync(new ConfirmTotpCommand(userId, tenantId, request.Code), ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new { mfaConfirmed = true });
    }

    /// <summary>Sign-in step 2: the code from the authenticator app turns the temporary token into an access token.</summary>
    [HttpPost("verify")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Verify([FromBody] CodeRequest request, CancellationToken ct)
    {
        if (!TryGetIdentity(out var userId, out var tenantId, out var version)) return Fail(InvalidToken);

        var result = await verify.HandleAsync(new VerifyMfaCommand(userId, tenantId, version, request.Code), ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new
        {
            accessToken = result.Value.AccessToken,
            tokenType = "Bearer",
            expiresIn = auth.Value.AccessTokenMinutes * 60
        });
    }

    private static readonly Error InvalidToken = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    private bool TryGetIdentity(out Guid userId, out Guid tenantId, out int tokenVersion)
    {
        var ok = Guid.TryParse(User.FindFirst("sub")?.Value, out userId)
               & Guid.TryParse(User.FindFirst("tid")?.Value, out tenantId)
               & int.TryParse(User.FindFirst("ver")?.Value, out tokenVersion);
        return ok && userId != Guid.Empty && tenantId != Guid.Empty;
    }
}