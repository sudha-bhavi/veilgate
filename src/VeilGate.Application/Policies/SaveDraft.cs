using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Policies;

public sealed record SaveDraftCommand(Guid PolicyId, string Yaml, string? IfMatch, Guid ActorId);
public sealed record SavedDraft(Guid VersionId, int VersionNo, string Etag);

/// <summary>
/// Saves the policy text as the draft: edits the existing draft, or starts a new one when there is none.
/// The caller must prove which text they were looking at (If-Match), so two people cannot silently overwrite each other.
/// A draft may hold text that does not validate yet; validation is checked again at submit and at publish.
/// </summary>
public sealed class SaveDraftHandler(IPolicyRepository policies)
{
    public async Task<Result<SavedDraft>> HandleAsync(SaveDraftCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Yaml) || cmd.Yaml.Length > PolicyParser.MaxYamlChars)
            return Error.Validation("validation.failed", $"Policy text must be 1 to {PolicyParser.MaxYamlChars} characters.");

        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Error.NotFound();
        if (policy.IsArchived) return Error.Conflict("resource.conflict", "An archived policy cannot be changed.");

        var (draft, current) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        if (policy.DraftVersionId is not null && draft is null) return Error.NotFound();

        if (!string.Equals(cmd.IfMatch?.Trim(), PolicyEtag.Of(draft ?? current), StringComparison.Ordinal))
            return Error.PreconditionFailed();

        if (draft is null)
        {
            var created = policy.NewDraft(cmd.Yaml, cmd.ActorId);
            if (created.IsFailure) return created.Error;
            policies.AddVersion(created.Value);
            draft = created.Value;
        }
        else
        {
            var edited = draft.EditDraft(cmd.Yaml);   // refused once the draft has been submitted
            if (edited.IsFailure) return edited.Error;
        }

        await policies.SaveChangesAsync(ct);
        return new SavedDraft(draft.Id, draft.VersionNo, PolicyEtag.Of(draft));
    }
}
