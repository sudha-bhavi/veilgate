using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;
using VeilGate.Application.Dashboard;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The Dashboard numbers, read from the hourly counters the metrics consumer keeps (Backend doc 5.3). Same audience as the request
/// list: every person of the tenant, because the numbers are counts only. The counters lag a moment behind the gateway.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Policy = AuthPolicies.RequestReader)]
public sealed class DashboardController : ApiControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromServices] GetDashboardSummaryHandler handler, CancellationToken ct, [FromQuery] string? range = null)
    {
        var summary = await handler.HandleAsync(range, ct);
        return summary.IsFailure ? Fail(summary.Error) : Ok(summary.Value);
    }

    [HttpGet("timeseries")]
    public async Task<IActionResult> Series([FromServices] GetDashboardSeriesHandler handler, CancellationToken ct,
        [FromQuery] string? range = null, [FromQuery] string? metric = null)
    {
        var series = await handler.HandleAsync(range, metric, ct);
        return series.IsFailure ? Fail(series.Error) : Ok(series.Value);
    }
}
