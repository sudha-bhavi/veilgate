using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using VeilGate.Api.Options;
using VeilGate.Api.Security;
using VeilGate.Application.Auth;

namespace VeilGate.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public sealed class AuthController(
    LoginHandler login, RefreshHandler refresh, LogoutHandler logout, IOptions<AuthOptions> auth) : ApiControllerBase
{
    public sealed record LoginRequest(string Email, string Password);

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await login.HandleAsync(new LoginCommand(request.Email, request.Password, CallerIp), ct);
        if (result.IsFailure) return Fail(result.Error);
        var r = result.Value;
        return Ok(new { mfaRequired = r.MfaRequired, mfaEnrollmentRequired = r.MfaEnrollmentRequired, mfaToken = r.MfaToken });
    }

    // No rate limit like login: a refresh token is 256 random bits, so guessing one is not a real attack.
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var result = await refresh.HandleAsync(new RefreshCommand(RefreshCookie.Read(Request)), ct);
        if (result.IsFailure)
        {
            RefreshCookie.Clear(Response);   // the token is dead, so tell the browser to drop it
            return Fail(result.Error);
        }

        RefreshCookie.Write(Response, result.Value.RefreshToken, result.Value.RefreshExpiresAt);
        return Ok(new { accessToken = result.Value.AccessToken, tokenType = "Bearer", expiresIn = auth.Value.AccessTokenMinutes * 60 });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await logout.HandleAsync(new LogoutCommand(RefreshCookie.Read(Request), CallerIp), ct);
        RefreshCookie.Clear(Response);
        return NoContent();
    }
}