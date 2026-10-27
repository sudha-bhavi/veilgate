using VeilGate.Domain.Common;

namespace VeilGate.Domain.Evaluation;

public enum EvalRunState { Queued, Running, Succeeded, Failed }

/// <summary>Detection quality for one finished run. The per-type breakdown is kept as JSON because its keys are entity types.</summary>
public sealed record EvalRunResult(double Recall, double Precision, double F1, double LeakRate, int Leaks, bool GatePassed, string PerTypeJson);

/// <summary>
/// One scoring run of a policy version over a dataset (Backend doc 5.3). A worker picks it up from the queue, so the life cycle
/// is strict: Queued -> Running -> Succeeded or Failed, and a finished run never changes again.
/// </summary>
public sealed class EvalRun : TenantEntity
{
    public const double DefaultRecallThreshold = 0.98;
    public const int MaxErrorLength = 200;

    private EvalRun() { }

    public Guid DatasetId { get; private set; }
    public Guid PolicyId { get; private set; }
    public int PolicyVersion { get; private set; }
    public string Mode { get; private set; } = Modes.Rules;
    public double RecallThreshold { get; private set; } = DefaultRecallThreshold;
    public Guid RequestedBy { get; private set; }

    public EvalRunState State { get; private set; } = EvalRunState.Queued;
    public int Attempts { get; private set; }
    public int TotalCases { get; private set; }
    public int DoneCases { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? ErrorCode { get; private set; }
        public EvalRunResult? Summary { get; private set; }

    public static class Modes
    {
        public const string Rules = "rules";
        public const string RulesAndNer = "rules+ner";
        public static bool IsValid(string? mode) => mode is Rules or RulesAndNer;
    }

    public static Result<EvalRun> Queue(
        Guid tenantId, Guid datasetId, Guid policyId, int policyVersion, string mode, double recallThreshold, Guid requestedBy, int totalCases)
    {
        if (tenantId == Guid.Empty || datasetId == Guid.Empty || policyId == Guid.Empty || requestedBy == Guid.Empty)
            return Error.Validation("validation.failed", "Tenant, dataset, policy and requester are required.");
        if (policyVersion < 1) return Error.Validation("validation.failed", "Policy version is required.");
        if (!Modes.IsValid(mode)) return Error.Validation("validation.failed", "Mode must be rules or rules+ner.");
        if (recallThreshold is < 0 or > 1) return Error.Validation("validation.failed", "Recall threshold must be between 0 and 1.");
        if (totalCases < 1) return Error.Validation("validation.failed", "The dataset has no notes.");

        var run = new EvalRun
        {
            DatasetId = datasetId, PolicyId = policyId, PolicyVersion = policyVersion, Mode = mode,
            RecallThreshold = recallThreshold, RequestedBy = requestedBy, TotalCases = totalCases
        };
        run.SetTenant(tenantId);
        return run;
    }

    /// <summary>A worker takes the run. Only a queued run can be taken, so two workers cannot both run it.</summary>
    public Result Start(DateTimeOffset now)
    {
        if (State != EvalRunState.Queued) return Result.Failure(Error.Conflict("evaluation.not_queued", "This run is not waiting to start."));
        State = EvalRunState.Running;
        Attempts++;
        StartedAt = now;
        return Result.Success();
    }

    public Result ReportProgress(int doneCases)
    {
        if (State != EvalRunState.Running) return Result.Failure(Error.Conflict("evaluation.not_running", "This run is not running."));
        DoneCases = Math.Clamp(doneCases, DoneCases, TotalCases);   // progress never goes backwards
        return Result.Success();
    }

    public Result Complete(EvalRunResult result, DateTimeOffset now)
    {
        if (State != EvalRunState.Running) return Result.Failure(Error.Conflict("evaluation.not_running", "This run is not running."));
        State = EvalRunState.Succeeded;
        DoneCases = TotalCases;
              Summary = result;
        FinishedAt = now;
        return Result.Success();
    }

    /// <summary>Records why it stopped as a short code, never a message that could carry text from a note.</summary>
    public Result Fail(string errorCode, DateTimeOffset now)
    {
        if (State is EvalRunState.Succeeded or EvalRunState.Failed)
            return Result.Failure(Error.Conflict("evaluation.finished", "This run is already finished."));
        State = EvalRunState.Failed;
        ErrorCode = errorCode.Length > MaxErrorLength ? errorCode[..MaxErrorLength] : errorCode;
        FinishedAt = now;
        return Result.Success();
    }

    public bool IsFinished => State is EvalRunState.Succeeded or EvalRunState.Failed;
}