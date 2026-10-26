using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Api.Gateway;
using VeilGate.Api.Security;
using VeilGate.Application.Gateway;
using VeilGate.Application.Llm;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The console's try-it-out box (Backend doc 4.1). A signed-in Developer, Security Officer or Tenant Admin runs the same pipeline
/// the gateway uses and sees each stage: what was found, what the model was given, what it answered, and what the caller gets back.
/// It uses the same protections and the same audit trail as a real call.
/// </summary>
[ApiController]
[Route("api/v1/playground")]
[Consumes("application/json")]
[Authorize(Policy = AuthPolicies.Playground)]
[EnableRateLimiting("gateway")]
public sealed class PlaygroundController(IGatewayPipeline gateway) : ApiControllerBase
{
    private static readonly TimeSpan VaultTtl = TimeSpan.FromMinutes(5);   // an experiment does not need an hour of stored originals

    [HttpPost("run")]
    [RequestSizeLimit(262_144)]
    public async Task<IActionResult> Run([FromBody] PlaygroundRequest request, CancellationToken ct)
    {
        var caller = HttpContext.ToGatewayCaller();

        switch (request.Mode)
        {
            case "detect":
            {
                var result = await gateway.DetectAsync(caller, request.Text, 0.5, ct);
                if (result.IsFailure) return Fail(result.Error);
                return Ok(new
                {
                    requestId = result.Value.RequestId,
                    entities = result.Value.Entities.Select(e => new { type = e.Type, start = e.Start, end = e.End, confidence = e.Confidence }),
                });
            }
            case "deidentify":
            {
                var result = await gateway.DeidentifyAsync(caller, request.Text, request.PolicyId, null, VaultTtl, ct);
                if (result.IsFailure) return Fail(result.Error);
                return Ok(new
                {
                    requestId = result.Value.RequestId,
                    entities = result.Value.Entities.Select(e => new { type = e.Type, start = e.Start, end = e.End, action = e.Action.ToString().ToUpperInvariant() }),
                    maskedText = result.Value.Text,
                });
            }
            default:   // "chat": the validator allows nothing else
            {
                var input = new ChatInput(request.Model!, [new LlmMessage("user", request.Text)]);
                var result = await gateway.ChatAsync(caller, input, request.PolicyId, ct);
                if (result.IsFailure) return Fail(result.Error);
                return Ok(new
                {
                    requestId = result.Value.RequestId,
                    entitiesProtected = result.Value.EntitiesProtected,
                    maskedText = result.Value.SanitizedMessages[0].Content,
                    llmResponseMasked = result.Value.MaskedAnswer,
                    restoredResponse = result.Value.Content,
                });
            }
        }
    }
}
