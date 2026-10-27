using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

public sealed record CreatePolicyCommand(string Name, string Template, string? Description, PolicyActor Actor);
public sealed record CreatedPolicy(Guid Id, Guid DraftVersionId, string Etag);

/// <summary>Creates a policy with a first draft copied from a template (blank, hipaa-safe-harbor, names-only).</summary>
public sealed class CreatePolicyHandler(IPolicyRepository policies, ITenantContext tenant, PolicyAuditor auditor)
{
    public async Task<Result<CreatedPolicy>> HandleAsync(CreatePolicyCommand cmd, CancellationToken ct = default)
    {
        if (!PolicyTemplates.Names.Contains(cmd.Template))
            return Error.Validation("validation.failed", "Unknown template.");

        var policy = Policy.Create(tenant.TenantId, cmd.Name, cmd.Description);
        if (policy.IsFailure) return policy.Error;

        if (await policies.NameExistsAsync(policy.Value.Name, ct))
            return Error.Conflict("resource.conflict", "A policy with this name already exists.");

        var draft = policy.Value.NewDraft(PolicyTemplates.YamlOf(cmd.Template), cmd.Actor.UserId);
        if (draft.IsFailure) return draft.Error;

        policies.AddPolicy(policy.Value);
        policies.AddVersion(draft.Value);
        await policies.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "policy.create", policy.Value.Id, "success", null, ct);
        return new CreatedPolicy(policy.Value.Id, draft.Value.Id, PolicyEtag.Of(draft.Value));
    }
}
