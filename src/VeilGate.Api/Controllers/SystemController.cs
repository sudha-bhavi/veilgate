using Microsoft.AspNetCore.Mvc;

namespace VeilGate.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
    /// <summary>Public, non-sensitive build information. Safe for smoke tests.</summary>
    [HttpGet("info")]
    public IActionResult Info() => Ok(new
    {
        name = "VeilGate",
        version = typeof(SystemController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        environment = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName
    });
}
