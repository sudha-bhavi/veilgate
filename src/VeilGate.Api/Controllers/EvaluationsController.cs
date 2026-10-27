using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Evaluation;
using VeilGate.Api.Security;
using VeilGate.Application.Evaluation;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// Measures how well a policy version protects a dataset (Backend doc 5.3). Starting a run only queues it; a worker does the work,
/// so the run is read back by polling. An evaluation of another tenant is simply "not found".
/// </summary>
[ApiController]
[Route("api/v1/evaluations")]
[Authorize(Policy = AuthPolicies.EvaluationOperator)]
public sealed class EvaluationsController : ApiControllerBase
{
    private EvaluationActor Actor => new(Guid.Parse(User.FindFirst("sub")!.Value), HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Create([FromBody] CreateEvaluationRequest request, [FromServices] CreateEvaluationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreateEvaluationCommand(
            request.DatasetId, request.PolicyId, request.PolicyVersion, request.Mode, request.RecallThreshold, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Accepted($"/api/v1/evaluations/{result.Value.Id}", result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromServices] ListEvaluationsHandler handler, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(new { items = await handler.HandleAsync(page, pageSize, ct), page, pageSize });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] GetEvaluationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpGet("{id:guid}/leaks")]
    public async Task<IActionResult> Leaks(Guid id, [FromServices] GetEvaluationLeaksHandler handler, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await handler.HandleAsync(id, page, pageSize, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(new { items = result.Value, page, pageSize });
    }

    /// <summary>The finished result as JSON (default) or as a Markdown document (format=md).</summary>
    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, [FromServices] GetEvaluationReportHandler handler, CancellationToken ct, [FromQuery] string format = "json")
    {
        if (format is not ("json" or "md")) return Fail(Error.Validation("validation.failed", "Format must be json or md."));
        var result = await handler.HandleAsync(id, ct);
        if (result.IsFailure) return Fail(result.Error);
        return format == "md" ? Content(EvaluationReportMarkdown.Render(result.Value), "text/markdown; charset=utf-8") : Ok(result.Value);
    }

    /// <summary>Run b against run a (same dataset). Positive deltas mean b is higher.</summary>
    [HttpGet("compare")]
    public async Task<IActionResult> Compare([FromQuery] Guid a, [FromQuery] Guid b, [FromServices] CompareEvaluationsHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(a, b, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }
}
