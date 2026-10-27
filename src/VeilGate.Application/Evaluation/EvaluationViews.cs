using System.Text.Json;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Application.Evaluation;

/// <summary>Counts and ratios for one entity type, as stored in a finished run.</summary>
public sealed record TypeReport(int Tp, int Fp, int Fn, double Precision, double Recall);

public sealed record EvaluationProgress(int Done, int Total);

public sealed record EvaluationResultView(
    double Recall, double Precision, double F1, double LeakRate, int Leaks, bool GatePassed, IReadOnlyDictionary<string, TypeReport> PerType)
{
    public static EvaluationResultView From(EvalRunResult r) => new(
        r.Recall, r.Precision, r.F1, r.LeakRate, r.Leaks, r.GatePassed,
        JsonSerializer.Deserialize<Dictionary<string, TypeReport>>(r.PerTypeJson, EvaluationRunner.Json) ?? []);
}

public sealed record EvaluationRunView(
    Guid Id, Guid DatasetId, Guid PolicyId, int PolicyVersion, string Mode, double RecallThreshold, string State, EvaluationProgress Progress,
    int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? ErrorCode, EvaluationResultView? Result)
{
    public static EvaluationRunView From(EvalRun r) => new(
        r.Id, r.DatasetId, r.PolicyId, r.PolicyVersion, r.Mode, r.RecallThreshold, r.State.ToString(),
        new EvaluationProgress(r.DoneCases, r.TotalCases), r.Attempts, r.CreatedAt, r.StartedAt, r.FinishedAt, r.ErrorCode,
        r.Summary is null ? null : EvaluationResultView.From(r.Summary));
}

public sealed record EvaluationReport(
    Guid RunId, string DatasetName, int Notes, string PolicyName, int PolicyVersion, string Mode, double RecallThreshold,
    DateTimeOffset? FinishedAt, EvaluationResultView Result, IReadOnlyList<LeakDetailView> Leaks, int LeaksShown);

public sealed record LeakDetailView(int? Note, string Type, int Start, int End, string Reason, string? Value);

public sealed record MetricChange(double A, double B, double Delta);

public sealed record TypeChange(MetricChange Recall, MetricChange Precision);

/// <summary>Run B against run A on the same dataset. Delta is B minus A: for recall, precision and F1 higher is better; for leaks, lower is.</summary>
public sealed record EvaluationComparison(
    Guid A, Guid B, Guid DatasetId, MetricChange Recall, MetricChange Precision, MetricChange F1, MetricChange LeakRate,
    int LeaksA, int LeaksB, bool GateA, bool GateB, IReadOnlyDictionary<string, TypeChange> PerType);
