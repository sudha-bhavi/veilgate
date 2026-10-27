using System.Globalization;
using System.Text;

namespace VeilGate.Application.Evaluation;

/// <summary>The evaluation report as a document a person can read, attach to a change request, or keep as evidence.</summary>
public static class EvaluationReportMarkdown
{
    public static string Render(EvaluationReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Evaluation report").AppendLine();
        sb.AppendLine($"**Gate: {(r.Result.GatePassed ? "PASS" : "FAIL")}** (recall at least {Pct(r.RecallThreshold)} and no leaks)").AppendLine();
        sb.AppendLine($"- Dataset: {r.DatasetName} ({r.Notes} notes)");
        sb.AppendLine($"- Policy: {r.PolicyName}, version {r.PolicyVersion}");
        sb.AppendLine($"- Detection: {r.Mode}");
        if (r.FinishedAt is { } finished) sb.AppendLine($"- Finished: {finished:u}");
        sb.AppendLine();

        sb.AppendLine("## Summary").AppendLine();
        sb.AppendLine($"- Recall: {Pct(r.Result.Recall)}");
        sb.AppendLine($"- Precision: {Pct(r.Result.Precision)}");
        sb.AppendLine($"- F1: {Pct(r.Result.F1)}");
        sb.AppendLine($"- Leaks: {r.Result.Leaks} ({Pct(r.Result.LeakRate)} of values the policy protects)").AppendLine();

        sb.AppendLine("## By type").AppendLine();
        sb.AppendLine("| Type | Found | False alarms | Missed | Precision | Recall |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (type, t) in r.Result.PerType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {type} | {t.Tp} | {t.Fp} | {t.Fn} | {Pct(t.Precision)} | {Pct(t.Recall)} |");
        sb.AppendLine();

        sb.AppendLine("## Leaks").AppendLine();
        if (r.Result.Leaks == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine($"Showing {r.LeaksShown} of {r.Result.Leaks}.").AppendLine();
            sb.AppendLine("| Note | Type | Position | Value | Reason |");
            sb.AppendLine("| ---: | --- | --- | --- | --- |");
            foreach (var l in r.Leaks)
                sb.AppendLine($"| {l.Note?.ToString() ?? "-"} | {l.Type} | {l.Start}-{l.End} | {(l.Value ?? "").Replace("|", "\\|")} | {l.Reason} |");
        }
        return sb.ToString();
    }

    private static string Pct(double value) => value.ToString("P1", CultureInfo.InvariantCulture);
}
