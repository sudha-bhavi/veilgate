using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VeilGate.Api.Options;
using VeilGate.Application.Auth;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// "Confirm it is you": a signed-in person enters an MFA code again and gets an access token for the same session with a
/// fresh auth_time, which is what publish and rollback ask for. A wrong code counts toward the sign-in lockout.
/// </summary>
[ApiController]
[Route("api/v1/auth/step-up")]
[Authorize]
public sealed class StepUpController(StepUpHandler stepUp, IOptions<AuthOptions> auth) : ApiControllerBase
{
    public sealed record StepUpRequest(string Code);

    private static readonly Error InvalidToken = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    [HttpPost]
    [EnableRateLimiting("login")]   // guessing 6-digit codes is a brute-force target, so this is limited per IP
    [Consumes("application/json")]
    public async Task<IActionResult> Post([FromBody] StepUpRequest request, CancellationToken ct)
    {
        var ok = Guid.TryParse(User.FindFirst("sub")?.Value, out var userId)
               & Guid.TryParse(User.FindFirst("tid")?.Value, out var tenantId)
               & Guid.TryParse(User.FindFirst("sid")?.Value, out var sessionId)
               & int.TryParse(User.FindFirst("ver")?.Value, out var tokenVersion);
        if (!ok) return Fail(InvalidToken);

        var result = await stepUp.HandleAsync(new StepUpCommand(userId, tenantId, sessionId, tokenVersion, request.Code, CallerIp), ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new { accessToken = result.Value.AccessToken, tokenType = "Bearer", expiresIn = auth.Value.AccessTokenMinutes * 60 });
    }
}