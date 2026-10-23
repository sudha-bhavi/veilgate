using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Application.Auth;

namespace VeilGate.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public sealed class AuthController(LoginHandler login) : ApiControllerBase
{
    public sealed record LoginRequest(string Email, string Password);

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await login.HandleAsync(new LoginCommand(request.Email, request.Password), ct);
        if (result.IsFailure) return Fail(result.Error);
        var r = result.Value;
        return Ok(new { mfaRequired = r.MfaRequired, mfaEnrollmentRequired = r.MfaEnrollmentRequired, mfaToken = r.MfaToken });
    }
}
