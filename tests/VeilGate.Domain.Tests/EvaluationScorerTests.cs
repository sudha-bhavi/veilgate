using FluentAssertions;
using VeilGate.Application.Evaluation;
using VeilGate.Application.Policies;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class EvaluationScorerTests
{
    private static readonly PolicyDocument Safe = PolicyTemplates.Get(PolicyTemplates.HipaaSafeHarbor)!;

    // A policy that leaves one type alone and redacts everything else.
    private static PolicyDocument Allowing(string type) => new()
    {
        Name = "allow-" + type,
        DefaultAction = PolicyAction.Redact,
        Entities = new Dictionary<string, EntityRule> { [type] = new(PolicyAction.Allow) }
    };

    private static DetectedEntity Found(string type, int start, int end, double confidence = 0.9) => new(type, start, end, confidence, "test");

    private static CaseScore Score(PolicyDocument policy, ExpectedEntity[] expected, params DetectedEntity[] found) =>
        EvaluationScorer.ScoreCase(expected, new PolicyEngine().Decide(found, policy), policy);

    // ------------------------------------------------------------ one note

    [Fact]
    public void Everything_found_exactly_scores_perfectly_with_no_leaks()
    {
        var score = Score(Safe, [new("SSN", 0, 11), new("PERSON", 20, 30)], Found("SSN", 0, 11), Found("PERSON", 20, 30));

        score.PerType["SSN"].Should().Be(new TypeCounts(1, 0, 0));
        score.PerType["PERSON"].Should().Be(new TypeCounts(1, 0, 0));
        score.ProtectedExpected.Should().Be(2);
        score.Leaks.Should().BeEmpty();
    }

    [Fact]
    public void A_value_nobody_found_is_a_false_negative_and_a_leak()
    {
        var ssn = new ExpectedEntity("SSN", 0, 11);

        var score = Score(Safe, [ssn]);

        score.PerType["SSN"].Should().Be(new TypeCounts(0, 0, 1));
        score.Leaks.Should().ContainSingle().Which.Should().Be(new Leak(ssn, LeakReason.NotDetected));
    }

    [Fact]
    public void Something_found_that_is_not_in_the_labels_is_a_false_positive_but_not_a_leak()
    {
        var score = Score(Safe, [], Found("PHONE", 5, 17));

        score.PerType["PHONE"].Should().Be(new TypeCounts(0, 1, 0));
        score.ProtectedExpected.Should().Be(0);
        score.Leaks.Should().BeEmpty();
    }

    [Fact]
    public void A_value_found_under_the_wrong_type_is_a_miss_and_a_false_alarm_but_still_protected()
    {
        var score = Score(Safe, [new("SSN", 0, 11)], Found("PERSON", 0, 11));

        score.PerType["SSN"].Should().Be(new TypeCounts(0, 0, 1));
        score.PerType["PERSON"].Should().Be(new TypeCounts(0, 1, 0));
        score.Leaks.Should().BeEmpty();   // the gateway would still have replaced it (PERSON is tokenized)
    }

    [Fact]
    public void Covering_at_least_half_of_a_value_counts_as_finding_it_but_the_rest_still_leaks()
    {
        var score = Score(Safe, [new("SSN", 0, 10)], Found("SSN", 0, 5));

        score.PerType["SSN"].Should().Be(new TypeCounts(1, 0, 0));
        score.Leaks.Should().ContainSingle().Which.Reason.Should().Be(LeakReason.PartlyProtected);
    }

    [Fact]
    public void Covering_less_than_half_does_not_count_as_finding_it()
    {
        var score = Score(Safe, [new("SSN", 0, 10)], Found("SSN", 0, 4));

        score.PerType["SSN"].Should().Be(new TypeCounts(0, 1, 1));
        score.Leaks.Should().ContainSingle().Which.Reason.Should().Be(LeakReason.PartlyProtected);
    }

    [Fact]
    public void A_type_the_policy_allows_is_never_a_leak_even_if_it_is_missed()
    {
        var score = Score(Allowing("PHONE"), [new("PHONE", 0, 12)]);

        score.PerType["PHONE"].Should().Be(new TypeCounts(0, 0, 1));   // still a detection failure...
        score.ProtectedExpected.Should().Be(0);                         // ...but not a privacy failure: the policy wanted it left alone
        score.Leaks.Should().BeEmpty();
    }

    [Fact]
    public void One_detection_can_serve_only_one_value()
    {
        var score = Score(Safe, [new("SSN", 0, 5), new("SSN", 5, 10)], Found("SSN", 0, 10));

        score.PerType["SSN"].Should().Be(new TypeCounts(1, 0, 1));
        score.Leaks.Should().BeEmpty();   // the single detection covers both values, so both are protected
    }

    [Fact]
    public void A_detection_below_the_policys_confidence_threshold_does_not_count()
    {
        var score = Score(Safe, [new("SSN", 0, 11)], Found("SSN", 0, 11, confidence: 0.2));

        score.PerType["SSN"].Should().Be(new TypeCounts(0, 0, 1));
        score.Leaks.Should().ContainSingle();
    }

    // ------------------------------------------------------------ many notes

    private static CaseScore PerfectNote() => Score(Safe, [new("SSN", 0, 11), new("PERSON", 20, 30)], Found("SSN", 0, 11), Found("PERSON", 20, 30));
    private static CaseScore NoteMissingAnSsn() => Score(Safe, [new("SSN", 0, 11)]);

    [Fact]
    public void Summarize_adds_up_the_notes_and_computes_recall_precision_and_f1()
    {
        var summary = EvaluationScorer.Summarize([PerfectNote(), NoteMissingAnSsn()], recallThreshold: 0.98);

        summary.PerType["SSN"].Should().Be(new TypeCounts(1, 0, 1));
        summary.PerType["PERSON"].Should().Be(new TypeCounts(1, 0, 0));
        summary.Recall.Should().BeApproximately(2.0 / 3, 1e-9);
        summary.Precision.Should().Be(1.0);
        summary.F1.Should().BeApproximately(0.8, 1e-9);
        summary.Leaks.Should().Be(1);
        summary.ProtectedExpected.Should().Be(3);
        summary.LeakRate.Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void The_gate_passes_when_recall_reaches_the_threshold_and_nothing_leaked()
        => EvaluationScorer.Summarize([PerfectNote(), PerfectNote()], 0.98).GatePassed.Should().BeTrue();

    [Fact]
    public void A_single_leak_fails_the_gate_however_good_the_recall_is()
        => EvaluationScorer.Summarize([PerfectNote(), NoteMissingAnSsn()], recallThreshold: 0.5).GatePassed.Should().BeFalse();

    [Fact]
    public void Low_recall_fails_the_gate_even_with_no_leaks()
    {
        var missedAllowedPhone = Score(Allowing("PHONE"), [new("PHONE", 0, 12)]);

        EvaluationScorer.Summarize([missedAllowedPhone], 0.98).GatePassed.Should().BeFalse();
        EvaluationScorer.Summarize([missedAllowedPhone], 0.0).GatePassed.Should().BeTrue();
    }

    [Fact]
    public void Nothing_to_score_gives_a_clean_summary_and_the_runner_refuses_empty_datasets_elsewhere()
    {
        var summary = EvaluationScorer.Summarize([], 0.98);

        (summary.Recall, summary.Precision, summary.F1, summary.LeakRate).Should().Be((1.0, 1.0, 1.0, 0.0));
        summary.GatePassed.Should().BeTrue();
    }
}