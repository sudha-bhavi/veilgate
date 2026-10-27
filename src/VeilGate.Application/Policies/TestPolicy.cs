using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Transformation;
using VeilGate.Domain.Common;
using VeilGate.Domain.Vault;

namespace VeilGate.Application.Policies;

public sealed record TestPolicyCommand(Guid PolicyId, string Text, Guid ActorId);
public sealed record TestedEntity(string Type, int Start, int End, double Confidence, string Action);

/// <summary>When a rule says BLOCK the real gateway would refuse the text, so there is no masked text to show.</summary>
public sealed record TestPolicyResult(IReadOnlyList<TestedEntity> Entities, string? MaskedText, bool Blocked, IReadOnlyList<string> BlockedTypes);

/// <summary>
/// Runs sample text through a policy exactly as the gateway would, without storing anything: no vault session is saved,
/// no request is recorded and nothing is sent to a model. It tests the draft if there is one, otherwise the published version.
/// Dates show as [REDACTED] here, because shifting needs a tenant key that only a real vault session carries.
/// </summary>
public sealed class TestPolicyHandler(
    IPolicyRepository policies, IDetectionPipeline detection, IPolicyEngine engine, ITransformer transformer,
    ITenantContext tenant, IClock clock)
{
    public async Task<Result<TestPolicyResult>> HandleAsync(TestPolicyCommand cmd, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Error.NotFound();

        var (draft, current) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        var version = draft ?? current;
        if (version is null) return Error.Conflict("policy.no_draft", "This policy has no text to test.");

        var parsed = PolicyParser.Parse(version.Definition);
        if (!parsed.IsValid)
            return Error.Unprocessable("policy.invalid_definition", "Fix the errors in the policy before testing it.").With("errors", parsed.Errors);
        var document = parsed.Policy!;

        if (cmd.Text.Length > document.MaxTextChars)
            return Error.Validation("validation.failed", $"Maximum {document.MaxTextChars} characters");

        var found = await detection.DetectAsync(cmd.Text, new DetectionContext { MinConfidence = document.MinConfidence }, ct);
        var decision = engine.Decide(found, document);
        var entities = decision.Items
            .OrderBy(i => i.Entity.Start)
            .Select(i => new TestedEntity(i.Entity.Type, i.Entity.Start, i.Entity.End, i.Entity.Confidence, i.Action.ToString().ToUpperInvariant()))
            .ToList();

        if (decision.IsBlocked) return new TestPolicyResult(entities, null, true, decision.BlockedTypes);

        var now = clock.UtcNow;
        var scratch = new VaultSession { TenantId = tenant.TenantId, ClientId = cmd.ActorId, CreatedAt = now, ExpiresAt = now.AddMinutes(1) };
        var transformed = transformer.Apply(cmd.Text, decision, scratch);   // scratch is never stored
        return new TestPolicyResult(entities, transformed.Text, false, []);
    }
}
