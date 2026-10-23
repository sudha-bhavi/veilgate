using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace VeilGate.Api.Controllers;

[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    /// <summary>Who am I? Everything comes from the verified token, which makes this a handy end-to-end check of authentication.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        userId = User.FindFirst("sub")?.Value,
        tenantId = User.FindFirst("tid")?.Value,
        role = User.FindFirst("role")?.Value,
        sessionId = User.FindFirst("sid")?.Value
    });
}
