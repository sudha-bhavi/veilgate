using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Llm;
using VeilGate.Application.Policies;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using VeilGate.Domain.Vault;

namespace VeilGate.Application.Gateway;

public sealed class GatewayPipeline(
    IDetectionPipeline detection,
    IPolicyEngine engine,
    ITransformer transformer,
    IOutboundLeakGate leakGate,
    IVaultService vault,
    IRehydrator rehydrator,
    ILlmGateway llm,
    IPolicyResolver policies,
    IAuditWriter audit,
    ISecurityEventWriter securityEvents,
    IClock clock,
    ILogger<GatewayPipeline> logger) : IGatewayPipeline
{
    private static string NewRequestId() => "req_" + Guid.NewGuid().ToString("N")[..8];

    // ---------------------------------------------------------------- detect

    public async Task<Result<DetectOutput>> DetectAsync(GatewayCaller caller, string text, double minConfidence, CancellationToken ct = default)
    {
        var requestId = NewRequestId();
        var found = await detection.DetectAsync(TextNormalizer.Normalize(text), new DetectionContext { MinConfidence = minConfidence }, ct);

        await AuditAsync(caller, "gateway.detect", requestId, "success", EntityDetails(found.Select(e => e.Type)), ct);
        return new DetectOutput(requestId, found);
    }

    // ----------------------------------------------------------- deidentify

    public async Task<Result<DeidentifyOutput>> DeidentifyAsync(
        GatewayCaller caller, string text, Guid? policyId, string? subjectId, TimeSpan? vaultTtl, CancellationToken ct = default)
    {
        var requestId = NewRequestId();
        var resolved = await policies.ResolveAsync(caller.TenantId, caller.ClientId, policyId, ct);
        if (resolved.IsFailure) return Result.Failure<DeidentifyOutput>(resolved.Error);
        var policy = resolved.Value.Document;

        var ttl = vaultTtl ?? TimeSpan.FromSeconds(policy.ReidentifyTtlSeconds);
        var session = await vault.OpenAsync(caller.TenantId, caller.ClientId, ttl, subjectId, ct);

        var protectedText = await ProtectAsync(caller, text, policy, session, requestId, ct);
        if (protectedText.IsFailure)
        {
            await DeniedAsync(caller, "gateway.deidentify", requestId, protectedText.Error, ct);
            return Result.Failure<DeidentifyOutput>(protectedText.Error);
        }

        await vault.StoreAsync(session, ct);

        var entities = protectedText.Value.Entities
            .Where(e => e.Action != PolicyAction.Allow)
            .Select(e => new ProtectedEntity(e.Type, e.Start, e.End, e.Action, e.Token))
            .ToList();
        var details = EntityDetails(entities.Select(e => e.Type));
        details["policyVersion"] = resolved.Value.Version.ToString();
        await AuditAsync(caller, "gateway.deidentify", requestId, "success", details, ct);

        return new DeidentifyOutput(requestId, protectedText.Value.Text, entities, session.Id, session.ExpiresAt);
    }

    // ----------------------------------------------------------- reidentify

    public async Task<Result<ReidentifyOutput>> ReidentifyAsync(GatewayCaller caller, Guid vaultSessionId, string text, CancellationToken ct = default)
    {
        var requestId = NewRequestId();
        var resolved = await policies.ResolveAsync(caller.TenantId, caller.ClientId, null, ct);
        if (resolved.IsFailure) return Result.Failure<ReidentifyOutput>(resolved.Error);

        // Two separate permissions: the policy must allow restoring at all, and this caller's token must carry the scope.
        if (!resolved.Value.Document.ReidentifyEnabled || !caller.CanReidentify)
        {
            var denied = Error.Forbidden("policy.reidentify_denied", "Re-identification is not allowed.");
            await DeniedAsync(caller, "gateway.reidentify", requestId, denied, ct);
            return Result.Failure<ReidentifyOutput>(denied);
        }

        // Wrong tenant, wrong client, expired and unknown all give this same answer.
        var session = await vault.FindAsync(vaultSessionId, caller.ClientId, ct);
        if (session is null) return Error.NotFound("vault.session_not_found", "Vault session not found or expired.");

        var restored = await rehydrator.RestoreAsync(text, session, ct);
        await AuditAsync(caller, "gateway.reidentify", requestId, "success",
            new Dictionary<string, string> { ["count"] = restored.Replaced.ToString() }, ct);
        return new ReidentifyOutput(restored.Text, restored.Replaced, restored.Unresolved);
    }

    // ----------------------------------------------------------------- chat

    public async Task<Result<ChatOutput>> ChatAsync(GatewayCaller caller, ChatInput input, Guid? policyId, CancellationToken ct = default)
    {
        var requestId = NewRequestId();
        var timer = Stopwatch.StartNew();

        var resolved = await policies.ResolveAsync(caller.TenantId, caller.ClientId, policyId, ct);
        if (resolved.IsFailure) return Result.Failure<ChatOutput>(resolved.Error);
        var policy = resolved.Value.Document;

        for (var i = 0; i < input.Messages.Count; i++)
            if (input.Messages[i].Content.Length > policy.MaxTextChars)
                return Error.Validation("validation.failed", $"messages[{i}].content: maximum {policy.MaxTextChars} characters")
                    .With("requestId", requestId);

        // 1. Protect every message, using one vault session so the same person gets the same token in every message.
        var session = await vault.OpenAsync(caller.TenantId, caller.ClientId, TimeSpan.FromSeconds(policy.ReidentifyTtlSeconds), null, ct);
        var sanitized = new List<LlmMessage>(input.Messages.Count);
        var protectedTypes = new List<string>();
        foreach (var message in input.Messages)
        {
            var protectedText = await ProtectAsync(caller, message.Content, policy, session, requestId, ct);
            if (protectedText.IsFailure)
            {
                await DeniedAsync(caller, "gateway.chat", requestId, protectedText.Error, ct);
                return Result.Failure<ChatOutput>(protectedText.Error);   // nothing has been sent to any provider
            }
            sanitized.Add(new LlmMessage(message.Role, protectedText.Value.Text));
            protectedTypes.AddRange(protectedText.Value.Entities.Where(e => e.Action != PolicyAction.Allow).Select(e => e.Type));
        }
        if (session.Tokens.Count > 0) await vault.StoreAsync(session, ct);

        // 2. Only sanitized text leaves here.
        var answer = await llm.CompleteAsync(new LlmRequest(input.Model, sanitized, input.Temperature, input.MaxTokens), ct);
        if (answer.IsFailure)
        {
            var failed = EntityDetails(protectedTypes);
            failed["reason"] = answer.Error.Code;
            await AuditAsync(caller, "gateway.chat", requestId, "failure", failed, ct);
            return Result.Failure<ChatOutput>(answer.Error.With("requestId", requestId));
        }

        // 3. Check the answer BEFORE restoring: restored originals are meant to be there and must not be flagged.
        var scanned = await ScanOutputAsync(caller, answer.Value.Content, policy, requestId, ct);
        if (scanned.IsFailure)
        {
            await DeniedAsync(caller, "gateway.chat", requestId, scanned.Error, ct);
            return Result.Failure<ChatOutput>(scanned.Error);
        }

        // 4. Put the originals back only if both the policy and the caller's token allow it.
        var content = scanned.Value;
        var restored = false;
        if (policy.ReidentifyEnabled && caller.CanReidentify && session.Tokens.Count > 0)
        {
            var result = Rehydrator.RestoreFrom(content, session.Tokens);
            content = result.Text;
            restored = result.Replaced > 0;
        }

        var details = EntityDetails(protectedTypes);
        details["policyVersion"] = resolved.Value.Version.ToString();
        details["model"] = input.Model;
        details["provider"] = answer.Value.Provider;
        details["latencyMs"] = timer.ElapsedMilliseconds.ToString();
        await AuditAsync(caller, "gateway.chat", requestId, "success", details, ct);

        var counts = protectedTypes.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        return new ChatOutput(requestId, answer.Value, content, restored, resolved.Value.Version, counts);
    }

    // --------------------------------------------------------- shared steps

    /// <summary>
    /// Normalize, detect, decide, transform, then run the leak gate on the result (Backend doc 6.1, steps 1 and 3 to 6).
    /// The gateway never asks "did something go wrong?"; it only returns text that has passed every check.
    /// </summary>
    private async Task<Result<TransformResult>> ProtectAsync(
        GatewayCaller caller, string rawText, PolicyDocument policy, VaultSession session, string requestId, CancellationToken ct)
    {
        var text = TextNormalizer.Normalize(rawText);
        var found = await detection.DetectAsync(text, DetectionContext.Default, ct);
        var decision = engine.Decide(found, policy);

        if (decision.IsBlocked)
            return Error.Unprocessable("policy.blocked", "The request contains data that the policy does not allow.")
                .With("entityTypes", decision.BlockedTypes)
                .With("requestId", requestId);

        var transformed = transformer.Apply(text, decision, session);

        var gate = await leakGate.CheckAsync(transformed, policy, DetectionContext.Default, ct);
        if (gate.Tripped)
        {
            logger.LogWarning("Leak gate tripped for request {RequestId}: {Types}", requestId, string.Join(",", gate.LeakedTypes));
            await RecordSecurityEventAsync(caller, Severity.High, "leak.gate_tripped", requestId, gate.LeakedTypes, ct);
            return Error.Unprocessable("leak.gate_tripped", "Sensitive data was still present after protection, so the request was stopped.")
                .With("requestId", requestId);
        }

        return transformed;
    }

    /// <summary>Looks for sensitive data in the model's answer (policy output.scan). Redacts it, or blocks the answer, per output.onLeak.</summary>
    private async Task<Result<string>> ScanOutputAsync(GatewayCaller caller, string content, PolicyDocument policy, string requestId, CancellationToken ct)
    {
        if (!policy.ScanOutput) return content;

        var found = await detection.DetectAsync(content, DetectionContext.Default, ct);
        var risky = found
            .Where(e => e.Confidence >= policy.MinConfidence && policy.RuleFor(e.Type).Action != PolicyAction.Allow)
            .ToList();
        if (risky.Count == 0) return content;

        await RecordSecurityEventAsync(caller, Severity.Medium, "output.leak", requestId, risky.Select(e => e.Type).Distinct().Order().ToList(), ct);

        if (policy.OutputOnLeak == PolicyAction.Block)
            return Error.Unprocessable("leak.gate_tripped", "The model's answer contained sensitive data, so it was withheld.").With("requestId", requestId);

        var text = new StringBuilder(content);
        foreach (var e in risky.OrderByDescending(e => e.Start))   // right to left, so earlier positions stay valid
            text.Remove(e.Start, e.Length).Insert(e.Start, "[REDACTED]");
        return text.ToString();
    }

    // --------------------------------------------------------- audit helpers

    private static Dictionary<string, string> EntityDetails(IEnumerable<string> types)
    {
        var list = types.ToList();
        var summary = string.Join(",", list.GroupBy(t => t).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));
        return new Dictionary<string, string>
        {
            ["count"] = list.Count.ToString(),
            ["entities"] = summary.Length > 200 ? summary[..200] : summary,   // counts per type, never values
        };
    }

    private async Task AuditAsync(GatewayCaller caller, string action, string requestId, string result, Dictionary<string, string> details, CancellationToken ct)
    {
        details["requestId"] = requestId;
        var created = AuditEvent.Create(caller.TenantId, clock.UtcNow, "client", caller.ClientId, action, "gateway_request", null, result, caller.Ip, details);
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }

    private Task DeniedAsync(GatewayCaller caller, string action, string requestId, Error error, CancellationToken ct) =>
        AuditAsync(caller, action, requestId, "denied", new Dictionary<string, string> { ["reason"] = error.Code }, ct);

    private async Task RecordSecurityEventAsync(GatewayCaller caller, Severity severity, string type, string requestId, IReadOnlyList<string> types, CancellationToken ct)
    {
        var joined = string.Join(",", types);
        var created = SecurityEvent.Create(caller.TenantId, clock.UtcNow, severity, type, clientId: caller.ClientId, ip: caller.Ip,
            details: new Dictionary<string, string> { ["requestId"] = requestId, ["types"] = joined.Length > 200 ? joined[..200] : joined });
        if (created.IsSuccess) await securityEvents.WriteAsync(created.Value, ct);
    }
}
