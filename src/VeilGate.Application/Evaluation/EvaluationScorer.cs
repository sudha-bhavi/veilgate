    using VeilGate.Application.Policies;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Evaluation;

/// <summary>A sensitive value that really is in a note, as labelled in the dataset. Positions only.</summary>
public sealed record ExpectedEntity(string Type, int Start, int End)
{
    public int Length => End - Start;
}

public static class LeakReason
{
    /// <summary>Nothing the policy acts on covers the value.</summary>
    public const string NotDetected = "not_detected";

    /// <summary>Something was found, but not the whole value: the rest would reach the model.</summary>
    public const string PartlyProtected = "partly_protected";
}

public sealed record Leak(ExpectedEntity Entity, string Reason);

/// <summary>True positives, false positives and false negatives for one entity type.</summary>
public sealed record TypeCounts(int Tp, int Fp, int Fn)
{
    // When there is nothing to find (or nothing was reported) the ratio is 1: no mistake was made.
    public double Precision => EvaluationScorer.Ratio(Tp, Tp + Fp);
    public double Recall => EvaluationScorer.Ratio(Tp, Tp + Fn);

    public static TypeCounts operator +(TypeCounts a, TypeCounts b) => new(a.Tp + b.Tp, a.Fp + b.Fp, a.Fn + b.Fn);
}

/// <param name="ProtectedExpected">How many expected values the policy would protect (its action is not ALLOW): the leak rate's denominator.</param>
public sealed record CaseScore(IReadOnlyDictionary<string, TypeCounts> PerType, int ProtectedExpected, IReadOnlyList<Leak> Leaks);

public sealed record EvaluationSummary(
    double Recall, double Precision, double F1, double LeakRate, int Leaks, int ProtectedExpected, bool GatePassed,
    IReadOnlyDictionary<string, TypeCounts> PerType);

/// <summary>
/// Scores detection against a labelled dataset (Backend doc 5.3, Database doc 4.2). It judges the whole path the gateway takes:
/// what the detectors found, filtered by the policy's confidence threshold, and what the policy then does with it.
/// Pure arithmetic over positions: no text, no database, easy to test.
/// </summary>
public static class EvaluationScorer
{
    /// <summary>A detection counts as finding a value if it overlaps at least this share of the value.</summary>
    public const double MinOverlap = 0.5;

    public static CaseScore ScoreCase(IReadOnlyList<ExpectedEntity> expected, PolicyDecision decision, PolicyDocument policy)
    {
        var real = expected.Where(e => e.Length > 0).OrderBy(e => e.Start).ThenBy(e => e.End).ToList();   // a label with no length is a data error: ignored
        var detected = decision.Items.Select(i => i.Entity).ToList();
        var used = new bool[detected.Count];
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);   // per type: [tp, fp, fn]
        int[] For(string type) => counts.TryGetValue(type, out var c) ? c : counts[type] = new int[3];

        // Pair each expected value with the detection of the same type that overlaps it most. A detection serves one value only.
        foreach (var e in real)
        {
            var best = -1;
            var bestOverlap = 0;
            for (var i = 0; i < detected.Count; i++)
            {
                if (used[i] || detected[i].Type != e.Type) continue;
                var overlap = Overlap(e.Start, e.End, detected[i].Start, detected[i].End);
                if (overlap >= e.Length * MinOverlap && overlap > bestOverlap) (best, bestOverlap) = (i, overlap);
            }

            if (best >= 0) { used[best] = true; For(e.Type)[0]++; }
            else For(e.Type)[2]++;
        }
        for (var i = 0; i < detected.Count; i++)
            if (!used[i]) For(detected[i].Type)[1]++;

        // Leaks: judged on what the policy does, not on the label, so a value found under the wrong type is still protected.
        var shielding = decision.Items.Where(i => i.Action != PolicyAction.Allow).Select(i => i.Entity).ToList();
        var leaks = new List<Leak>();
        var protectedCount = 0;
        foreach (var e in real)
        {
            if (policy.RuleFor(e.Type).Action == PolicyAction.Allow) continue;   // the policy chose to leave this type alone
            protectedCount++;

            var covered = CoveredLength(e, shielding);
            if (covered == e.Length) continue;
            leaks.Add(new Leak(e, covered == 0 ? LeakReason.NotDetected : LeakReason.PartlyProtected));
        }

        return new CaseScore(
            counts.ToDictionary(kv => kv.Key, kv => new TypeCounts(kv.Value[0], kv.Value[1], kv.Value[2])),
            protectedCount, leaks);
    }

    /// <summary>Adds up the cases. The gate passes only if recall reaches the threshold AND nothing leaked (Database doc 4.2).</summary>
    public static EvaluationSummary Summarize(IEnumerable<CaseScore> scores, double recallThreshold)
    {
        var perType = new SortedDictionary<string, TypeCounts>(StringComparer.Ordinal);
        var protectedExpected = 0;
        var leaks = 0;
        foreach (var score in scores)
        {
            protectedExpected += score.ProtectedExpected;
            leaks += score.Leaks.Count;
            foreach (var (type, counts) in score.PerType)
                perType[type] = perType.TryGetValue(type, out var sum) ? sum + counts : counts;
        }

        var tp = perType.Values.Sum(c => c.Tp);
        var fp = perType.Values.Sum(c => c.Fp);
        var fn = perType.Values.Sum(c => c.Fn);
        var recall = Ratio(tp, tp + fn);
        var precision = Ratio(tp, tp + fp);
        var f1 = recall + precision == 0 ? 0 : 2 * recall * precision / (recall + precision);
        var leakRate = protectedExpected == 0 ? 0 : (double)leaks / protectedExpected;

        return new EvaluationSummary(recall, precision, f1, leakRate, leaks, protectedExpected,
            GatePassed: recall >= recallThreshold && leaks == 0, perType);
    }

    internal static double Ratio(int part, int whole) => whole == 0 ? 1.0 : (double)part / whole;

    private static int Overlap(int aStart, int aEnd, int bStart, int bEnd) => Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));

    // How many characters of the value are covered by at least one detection the policy acts on.
    private static int CoveredLength(ExpectedEntity e, List<DetectedEntity> shielding)
    {
        var covered = new bool[e.Length];
        foreach (var d in shielding)
            for (var p = Math.Max(d.Start, e.Start); p < Math.Min(d.End, e.End); p++)
                covered[p - e.Start] = true;
        return covered.Count(c => c);
    }
}