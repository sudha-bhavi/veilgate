using System.Text.Json;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Policies;
using VeilGate.Domain;
using VeilGate.Domain.Evaluation;

namespace VeilGate.Application.Evaluation;

/// <summary>
/// Runs one claimed evaluation (Backend doc 5.3): every note goes through the same path the gateway uses (detect, then the policy
/// decides), the result is scored against the labels, and the leaks are stored. The tenant must already be set.
/// Anything that stops a run for a known reason (policy missing, empty dataset) is recorded on the run as a short code;
/// an unexpected exception is left to the caller, which marks the run failed.
/// </summary>
public sealed class EvaluationRunner(
    IEvaluationRepository evaluations, IPolicyRepository policies, IEnumerable<IDetector> detectors, IPolicyEngine engine, IClock clock)
{
    public const int BatchSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDetector[] _detectors = detectors.ToArray();

    private sealed record TypeReport(int Tp, int Fp, int Fn, double Precision, double Recall);

    public async Task RunAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await evaluations.FindRunAsync(runId, ct);
        if (run is null || run.State != EvalRunState.Running) return;   // not ours to run

        var version = await policies.FindVersionByNumberAsync(run.PolicyId, run.PolicyVersion, ct);
        if (version is null || version.TenantId != run.TenantId) { await FailAsync(run, "policy.not_found", ct); return; }
        // Any state may be evaluated (a draft too), but never text that was changed behind the application's back or no longer parses.
        var parsed = version.IsIntact ? PolicyParser.Parse(version.Definition) : null;
        if (parsed is not { IsValid: true }) { await FailAsync(run, "policy.invalid", ct); return; }
        var policy = parsed.Policy!;

        var pipeline = new DetectionPipeline(Select(run.Mode));
        await evaluations.DeleteLeaksAsync(run.Id, ct);

        var scores = new List<CaseScore>();
        var after = -1;
        while (true)
        {
            var batch = await evaluations.GetCasesAsync(run.DatasetId, after, BatchSize, ct);
            if (batch.Count == 0) break;

            var leaks = new List<EvalLeak>();
            foreach (var note in batch)
            {
                ct.ThrowIfCancellationRequested();
                var found = await pipeline.DetectAsync(note.Text, DetectionContext.Default, ct);
                var decision = engine.Decide(found, policy);
                var expected = note.Expected.Select(s => new ExpectedEntity(s.Type, s.Start, s.End)).ToList();

                var score = EvaluationScorer.ScoreCase(expected, decision, policy);
                scores.Add(score);
                leaks.AddRange(score.Leaks.Select(l =>
                    EvalLeak.Create(run.TenantId, run.Id, note.Id, l.Entity.Type, l.Entity.Start, l.Entity.End, l.Reason)));
            }

            evaluations.AddLeaks(leaks);
            run.ReportProgress(scores.Count);
            await evaluations.SaveChangesAsync(ct);   // saves this batch's leaks and the progress together
            after = batch[^1].Ordinal;
        }

        if (scores.Count == 0) { await FailAsync(run, "dataset.empty", ct); return; }

        var summary = EvaluationScorer.Summarize(scores, run.RecallThreshold);
        var perType = summary.PerType.ToDictionary(
            kv => kv.Key, kv => new TypeReport(kv.Value.Tp, kv.Value.Fp, kv.Value.Fn, kv.Value.Precision, kv.Value.Recall));

        run.Complete(new EvalRunResult(
            summary.Recall, summary.Precision, summary.F1, summary.LeakRate, summary.Leaks, summary.GatePassed,
            JsonSerializer.Serialize(perType, Json)), clock.UtcNow);
        await evaluations.SaveChangesAsync(ct);
    }

    /// <summary>"rules" is the pattern and dictionary detectors; "rules+ner" adds the name detector (the stand-in for the ONNX model).</summary>
    private IEnumerable<IDetector> Select(string mode) =>
        mode == EvalRun.Modes.RulesAndNer ? _detectors : _detectors.Where(d => d.Name is "regex" or "dict");

    private async Task FailAsync(EvalRun run, string code, CancellationToken ct)
    {
        run.Fail(code, clock.UtcNow);
        await evaluations.SaveChangesAsync(ct);
    }
}