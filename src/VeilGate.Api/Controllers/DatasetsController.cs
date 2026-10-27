using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Evaluation;
using VeilGate.Api.Security;
using VeilGate.Application.Evaluation;

namespace VeilGate.Api.Controllers;

/// <summary>
/// Labelled test data for evaluations (Backend doc 5.3). Datasets here are synthetic only: invented notes with known answers,
/// so nothing real is ever stored. A dataset of another tenant is simply "not found".
/// </summary>
[ApiController]
[Route("api/v1/datasets")]
[Authorize(Policy = AuthPolicies.EvaluationOperator)]
public sealed class DatasetsController : ApiControllerBase
{
    private EvaluationActor Actor => new(Guid.Parse(User.FindFirst("sub")!.Value), HttpContext.Connection.RemoteIpAddress?.ToString());

    [HttpGet]
    public async Task<IActionResult> List([FromServices] ListDatasetsHandler handler, CancellationToken ct) =>
        Ok(new { items = await handler.HandleAsync(ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] GetDatasetHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    /// <summary>Generates a new synthetic dataset. The same name is refused; the same seed gives the same notes.</summary>
    [HttpPost("synthetic")]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateSynthetic([FromBody] CreateSyntheticDatasetRequest request, [FromServices] CreateSyntheticDatasetHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreateSyntheticDatasetCommand(request.Name, request.Count, request.Seed, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Created($"/api/v1/datasets/{result.Value.Id}", result.Value);
    }
}