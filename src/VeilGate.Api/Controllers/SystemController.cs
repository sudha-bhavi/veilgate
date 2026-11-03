using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VeilGate.Api.Options;

namespace VeilGate.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
    /// <summary>Public, non-sensitive build information. Safe for smoke tests.</summary>
    [HttpGet("info")]
    public IActionResult Info([FromServices] IOptions<DemoOptions> demo) => Ok(new
    {
        name = "VeilGate",
        version = typeof(SystemController).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        environment = HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName,
        demoEnabled = demo.Value.Enabled   // lets the sign-in page decide whether to show "Try the demo"
    });
}
