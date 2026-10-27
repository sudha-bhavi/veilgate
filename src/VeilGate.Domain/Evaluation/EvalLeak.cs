using VeilGate.Domain.Common;

namespace VeilGate.Domain.Evaluation;

/// <summary>One sensitive value that a run failed to protect. Positions and a reason only: the note itself stays in <see cref="EvalCase"/>.</summary>
public sealed class EvalLeak : TenantEntity
{
    private EvalLeak() { }

    public Guid RunId { get; private set; }
    public Guid CaseId { get; private set; }
    public string EntityType { get; private set; } = string.Empty;
    public int Start { get; private set; }
    public int End { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    public static EvalLeak Create(Guid tenantId, Guid runId, Guid caseId, string entityType, int start, int end, string reason)
    {
        var leak = new EvalLeak { RunId = runId, CaseId = caseId, EntityType = entityType, Start = start, End = end, Reason = reason };
        leak.SetTenant(tenantId);
        return leak;
    }
}