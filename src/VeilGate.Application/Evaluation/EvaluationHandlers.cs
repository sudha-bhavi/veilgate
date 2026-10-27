using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Application.Evaluation;

public sealed record CreateEvaluationCommand(
    Guid DatasetId, Guid PolicyId, int? PolicyVersion, string? Mode, double? RecallThreshold, EvaluationActor Actor);

public sealed class CreateEvaluationHandler(IEvaluationRepository evaluations, IPolicyRepository policies, ITenantContext tenant, EvaluationAuditor auditor)
{
    public const int MaxActiveRuns = 5;

    public async Task<Result<EvaluationRunView>> HandleAsync(CreateEvaluationCommand cmd, CancellationToken ct = default)
    {
        var dataset = await evaluations.FindDatasetAsync(cmd.DatasetId, ct);
        if (dataset is null) return Error.NotFound("dataset.not_found", "Dataset not found.");
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Error.NotFound("policy.not_found", "Policy not found.");

        // Any version may be evaluated. Without a number: the published one, else the draft (so a change can be tried before it is submitted).
        var version = cmd.PolicyVersion is { } number
            ? await policies.FindVersionByNumberAsync(policy.Id, number, ct)
            : await policies.FindVersionAsync(policy.CurrentVersionId ?? policy.DraftVersionId ?? Guid.Empty, ct);
        if (version is null) return Error.NotFound("policy.version_not_found", "Policy version not found.");

        if (await evaluations.CountActiveRunsAsync(ct) >= MaxActiveRuns)
            return Error.Conflict("evaluation.too_many_active", $"At most {MaxActiveRuns} evaluations can wait or run at once.");

        var run = EvalRun.Queue(tenant.TenantId, dataset.Id, policy.Id, version.VersionNo, cmd.Mode ?? EvalRun.Modes.Rules,
            cmd.RecallThreshold ?? EvalRun.DefaultRecallThreshold, cmd.Actor.UserId, dataset.CaseCount);
        if (run.IsFailure) return run.Error;

        evaluations.AddRun(run.Value);
        await evaluations.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "evaluation.create", "eval_run", run.Value.Id, ct);
        return EvaluationRunView.From(run.Value);
    }
}

public sealed class ListEvaluationsHandler(IEvaluationRepository evaluations)
{
    public async Task<IReadOnlyList<EvaluationRunView>> HandleAsync(int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return (await evaluations.ListRunsAsync((page - 1) * pageSize, pageSize, ct)).Select(EvaluationRunView.From).ToList();
    }
}

public sealed class GetEvaluationHandler(IEvaluationRepository evaluations)
{
    public async Task<Result<EvaluationRunView>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var run = await evaluations.FindRunAsync(id, ct);
        return run is null ? Error.NotFound() : EvaluationRunView.From(run);
    }
}

public sealed class GetEvaluationLeaksHandler(IEvaluationRepository evaluations)
{
    public async Task<Result<IReadOnlyList<LeakDetail>>> HandleAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        if (await evaluations.FindRunAsync(id, ct) is null) return Error.NotFound();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var leaks = await evaluations.ListLeakDetailsAsync(id, (page - 1) * pageSize, pageSize, ct);
        return Result.Success(leaks);   // an interface type cannot convert implicitly to Result<T>, so it is wrapped explicitly
    }
}

public sealed class GetEvaluationReportHandler(IEvaluationRepository evaluations, IPolicyRepository policies)
{
    public const int LeaksInReport = 50;

    public async Task<Result<EvaluationReport>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var run = await evaluations.FindRunAsync(id, ct);
        if (run is null) return Error.NotFound();
        if (run.State != EvalRunState.Succeeded || run.Summary is null)
            return Error.Conflict("evaluation.not_finished", "This evaluation has no result yet.");

        var dataset = await evaluations.FindDatasetAsync(run.DatasetId, ct);
        var policy = await policies.FindAsync(run.PolicyId, ct);
        var leaks = await evaluations.ListLeakDetailsAsync(run.Id, 0, LeaksInReport, ct);

        return new EvaluationReport(
            run.Id, dataset?.Name ?? "(unknown)", run.TotalCases, policy?.Name ?? "(unknown)", run.PolicyVersion, run.Mode, run.RecallThreshold,
            run.FinishedAt, EvaluationResultView.From(run.Summary),
            leaks.Select(l => new LeakDetailView(l.CaseOrdinal, l.EntityType, l.Start, l.End, l.Reason, l.Value)).ToList(), leaks.Count);
    }
}

public sealed class CompareEvaluationsHandler(IEvaluationRepository evaluations)
{
    private static readonly TypeReport Nothing = new(0, 0, 0, 1, 1);   // a type absent from a run: nothing to find, nothing found

    public async Task<Result<EvaluationComparison>> HandleAsync(Guid a, Guid b, CancellationToken ct = default)
    {
        var runA = await evaluations.FindRunAsync(a, ct);
        var runB = await evaluations.FindRunAsync(b, ct);
        if (runA is null || runB is null) return Error.NotFound();
        if (runA.Summary is null || runB.Summary is null || runA.State != EvalRunState.Succeeded || runB.State != EvalRunState.Succeeded)
            return Error.Conflict("evaluation.not_finished", "Both evaluations must be finished.");
        if (runA.DatasetId != runB.DatasetId)
            return Error.Unprocessable("evaluation.different_datasets", "Only evaluations of the same dataset can be compared.");

        var ra = EvaluationResultView.From(runA.Summary);
        var rb = EvaluationResultView.From(runB.Summary);
        var perType = ra.PerType.Keys.Union(rb.PerType.Keys).Order(StringComparer.Ordinal).ToDictionary(
            type => type,
            type =>
            {
                var x = ra.PerType.GetValueOrDefault(type, Nothing);
                var y = rb.PerType.GetValueOrDefault(type, Nothing);
                return new TypeChange(Change(x.Recall, y.Recall), Change(x.Precision, y.Precision));
            });

        return new EvaluationComparison(
            runA.Id, runB.Id, runA.DatasetId, Change(ra.Recall, rb.Recall), Change(ra.Precision, rb.Precision), Change(ra.F1, rb.F1),
            Change(ra.LeakRate, rb.LeakRate), ra.Leaks, rb.Leaks, ra.GatePassed, rb.GatePassed, perType);
    }

    private static MetricChange Change(double a, double b) => new(a, b, b - a);
}
