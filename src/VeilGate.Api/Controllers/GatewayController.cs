using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Api.Gateway;
using VeilGate.Api.Security;
using VeilGate.Application.Gateway;
using VeilGate.Application.Llm;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The programmatic API (Backend doc 4.1). Callers are API clients holding a token with the matching scope.
/// Controllers only translate: they read the caller from the verified token, call the pipeline and shape the answer.
/// All business rules live in <see cref="IGatewayPipeline"/>.
/// </summary>
[ApiController]
[Route("api/v1/gateway")]
[Consumes("application/json")]          // anything else is 415
[EnableRateLimiting("gateway")]
public sealed class GatewayController(IGatewayPipeline gateway) : ApiControllerBase
{
    private const double DefaultMinConfidence = 0.5;

    /// <summary>Finds entities and reports where they are. The text is never sent back.</summary>
    [HttpPost("detect")]
    [Authorize(Policy = "Scope:gateway:detect")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Detect([FromBody] DetectRequest request, CancellationToken ct)
    {
        var result = await gateway.DetectAsync(HttpContext.ToGatewayCaller(), request.Text, request.Options?.MinConfidence ?? DefaultMinConfidence, ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new
        {
            requestId = result.Value.RequestId,
            entities = result.Value.Entities.Select(e => new { type = e.Type, start = e.Start, end = e.End, confidence = e.Confidence, detector = e.Detector }),
        });
    }

    /// <summary>Returns text that is safe to share, plus a vault session for restoring tokens later.</summary>
    [HttpPost("deidentify")]
    [Authorize(Policy = "Scope:gateway:deidentify")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Deidentify([FromBody] DeidentifyRequest request, CancellationToken ct)
    {
        TimeSpan? ttl = request.VaultTtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        var result = await gateway.DeidentifyAsync(HttpContext.ToGatewayCaller(), request.Text, request.PolicyId, request.SubjectId, ttl, ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new
        {
            requestId = result.Value.RequestId,
            text = result.Value.Text,
            entities = result.Value.Entities.Select(e => new { type = e.Type, start = e.Start, end = e.End, action = e.Action.ToString().ToUpperInvariant(), token = e.Token }),
            vaultSessionId = result.Value.VaultSessionId,
            expiresAt = result.Value.ExpiresAt.UtcDateTime,
        });
    }

    /// <summary>Puts the original values back where tokens appear. Needs both the scope and a policy that allows it.</summary>
    [HttpPost("reidentify")]
    [Authorize(Policy = "Scope:gateway:reidentify")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Reidentify([FromBody] ReidentifyRequest request, CancellationToken ct)
    {
        var result = await gateway.ReidentifyAsync(HttpContext.ToGatewayCaller(), request.VaultSessionId, request.Text, ct);
        if (result.IsFailure) return Fail(result.Error);

        return Ok(new { text = result.Value.Text, replaced = result.Value.Replaced, unresolved = result.Value.Unresolved });
    }

    /// <summary>OpenAI-compatible chat: protect, forward, check, restore.</summary>
    [HttpPost("chat/completions")]
    [Authorize(Policy = "Scope:gateway:invoke")]
    [RequestSizeLimit(262_144)]   // 256 KB, Backend doc 2.1
    public async Task<IActionResult> ChatCompletions([FromBody] ChatCompletionRequest request, CancellationToken ct)
    {
        Guid? policyId = null;
        if (Request.Headers.TryGetValue("X-VeilGate-Policy", out var header))
        {
            if (!Guid.TryParse(header.ToString(), out var parsed))
                return Fail(Error.Unprocessable("validation.failed", "X-VeilGate-Policy must be a policy id."));
            policyId = parsed;
        }

        var input = new ChatInput(request.Model, request.Messages.Select(m => new LlmMessage(m.Role, m.Content)).ToList(), request.Temperature, request.MaxTokens);
        var result = await gateway.ChatAsync(HttpContext.ToGatewayCaller(), input, policyId, ct);
        if (result.IsFailure) return Fail(result.Error);

        var r = result.Value;
        return Ok(new
        {
            id = r.Response.Id,
            @object = "chat.completion",
            model = r.Response.Model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = r.Content }, finish_reason = r.Response.FinishReason } },
            usage = new
            {
                prompt_tokens = r.Response.PromptTokens,
                completion_tokens = r.Response.CompletionTokens,
                total_tokens = r.Response.PromptTokens + r.Response.CompletionTokens,
            },
            veilgate = new { requestId = r.RequestId, policyVersion = r.PolicyVersion, entitiesProtected = r.EntitiesProtected, restored = r.Restored },
        });
    }
}
