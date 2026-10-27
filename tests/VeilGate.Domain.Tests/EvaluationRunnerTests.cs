using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Evaluation;
using VeilGate.Application.Policies;
using VeilGate.Domain;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Evaluation;
using VeilGate.Domain.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class EvaluationRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    /// <summary>A detector that "finds" exactly what the test scripted for a given note text.</summary>
    private sealed class ScriptedDetector(string name, Dictionary<string, DetectedEntity[]> script) : IDetector
    {
        public string Name => name;
        public IEnumerable<DetectedEntity> Detect(string text, DetectionContext context) => script.TryGetValue(text, out var found) ? found : [];
    }

    private sealed class Rig
    {
        public IEvaluationRepository Evaluations { get; } = Substitute.For<IEvaluationRepository>();
        public IPolicyRepository Policies { get; } = Substitute.For<IPolicyRepository>();
        public List<EvalLeak> SavedLeaks { get; } = [];
        public List<EvalCase> Cases { get; } = [];
        public EvalRun Run { get; }
        public EvaluationRunner Runner { get; }

        /// <param name="foundEvery">The scripted detector finds the SSN in every Nth note (1 = all, 0 = none).</param>
        public Rig(int notes, int foundEvery = 1, string mode = EvalRun.Modes.Rules, string detectorName = "regex",
            string? policyYaml = null, bool policyExists = true, bool started = true)
        {
            var datasetId = Guid.NewGuid();
            var script = new Dictionary<string, DetectedEntity[]>();
            for (var i = 0; i < notes; i++)
            {
                var text = $"Note {i}: SSN 123-45-6789.";
                var start = text.IndexOf("123", StringComparison.Ordinal);
                Cases.Add(EvalCase.Create(Tenant, datasetId, i, text, [new LabeledSpan("SSN", start, start + 11)]).Value);
                if (foundEvery > 0 && i % foundEvery == 0)
                    script[text] = [new DetectedEntity("SSN", start, start + 11, 0.95, detectorName)];
            }

            var policy = Policy.Create(Tenant, "p", null).Value;
            var version = policy.NewDraft(policyYaml ?? PolicyTemplates.YamlOf(PolicyTemplates.HipaaSafeHarbor), Guid.NewGuid()).Value;
            Run = EvalRun.Queue(Tenant, datasetId, policy.Id, version.VersionNo, mode, 0.98, Guid.NewGuid(), Math.Max(notes, 1)).Value;
            if (started) Run.Start(Now);

            Evaluations.FindRunAsync(Run.Id, Arg.Any<CancellationToken>()).Returns(Run);
            Evaluations.GetCasesAsync(datasetId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult<IReadOnlyList<EvalCase>>(
                    Cases.Where(c => c.Ordinal > ci.ArgAt<int>(1)).OrderBy(c => c.Ordinal).Take(ci.ArgAt<int>(2)).ToList()));
            Evaluations.When(r => r.AddLeaks(Arg.Any<IEnumerable<EvalLeak>>())).Do(ci => SavedLeaks.AddRange(ci.ArgAt<IEnumerable<EvalLeak>>(0)));
            Policies.FindVersionByNumberAsync(policy.Id, version.VersionNo, Arg.Any<CancellationToken>())
                .Returns(policyExists ? version : null);

            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            // The pattern detector is the one scripted; a second detector with the name "heuristic" is always present and finds nothing.
            IDetector[] detectors = [new ScriptedDetector(detectorName, script), new ScriptedDetector(detectorName == "regex" ? "heuristic" : "regex", [])];
            Runner = new EvaluationRunner(Evaluations, Policies, detectors, new PolicyEngine(), clock);
        }
    }

    [Fact]
    public async Task A_run_is_scored_and_the_leaks_it_found_are_stored()
    {
        var rig = new Rig(notes: 10, foundEvery: 2);   // even notes are found, odd notes leak

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.State.Should().Be(EvalRunState.Succeeded);
        rig.Run.DoneCases.Should().Be(10);
        rig.Run.Summary!.Recall.Should().BeApproximately(0.5, 1e-9);
        rig.Run.Summary.Precision.Should().Be(1.0);
        rig.Run.Summary.Leaks.Should().Be(5);
        rig.Run.Summary.GatePassed.Should().BeFalse();
        rig.SavedLeaks.Should().HaveCount(5);
        rig.SavedLeaks.Should().OnlyContain(l => l.RunId == rig.Run.Id && l.TenantId == Tenant && l.EntityType == "SSN" && l.Reason == LeakReason.NotDetected);

        var ssn = JsonNode.Parse(rig.Run.Summary.PerTypeJson)!["SSN"]!;
        ssn["tp"]!.GetValue<int>().Should().Be(5);
        ssn["fn"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task A_detector_that_finds_everything_passes_the_gate()
    {
        var rig = new Rig(notes: 10, foundEvery: 1);

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.Summary!.GatePassed.Should().BeTrue();
        rig.Run.Summary.Leaks.Should().Be(0);
        rig.SavedLeaks.Should().BeEmpty();
    }

    [Fact]
    public async Task Rules_mode_leaves_out_the_name_detector_and_rules_plus_ner_uses_it()
    {
        var rules = new Rig(notes: 4, mode: EvalRun.Modes.Rules, detectorName: "heuristic");
        var withNer = new Rig(notes: 4, mode: EvalRun.Modes.RulesAndNer, detectorName: "heuristic");

        await rules.Runner.RunAsync(rules.Run.Id);
        await withNer.Runner.RunAsync(withNer.Run.Id);

        rules.Run.Summary!.Leaks.Should().Be(4);   // the only detector that finds anything is not part of "rules"
        withNer.Run.Summary!.Leaks.Should().Be(0);
    }

    [Fact]
    public async Task Notes_are_read_in_batches_and_progress_is_saved_after_each()
    {
        var rig = new Rig(notes: 250);

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.State.Should().Be(EvalRunState.Succeeded);
        await rig.Evaluations.Received(4).SaveChangesAsync(Arg.Any<CancellationToken>());   // 100 + 100 + 50, then the result
    }

    [Fact]
    public async Task Leaks_from_an_abandoned_earlier_attempt_are_cleared_first()
    {
        var rig = new Rig(notes: 3);

        await rig.Runner.RunAsync(rig.Run.Id);

        await rig.Evaluations.Received(1).DeleteLeaksAsync(rig.Run.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_policy_fails_the_run_with_a_code_and_reads_no_notes()
    {
        var rig = new Rig(notes: 3, policyExists: false);

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.State.Should().Be(EvalRunState.Failed);
        rig.Run.ErrorCode.Should().Be("policy.not_found");
        await rig.Evaluations.DidNotReceive().GetCasesAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_policy_that_no_longer_parses_fails_the_run()
    {
        var rig = new Rig(notes: 3, policyYaml: "this: [is not valid");

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.ErrorCode.Should().Be("policy.invalid");
    }

    [Fact]
    public async Task A_dataset_with_no_notes_fails_the_run()
    {
        var rig = new Rig(notes: 3);
        rig.Cases.Clear();

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.State.Should().Be(EvalRunState.Failed);
        rig.Run.ErrorCode.Should().Be("dataset.empty");
    }

    [Fact]
    public async Task A_run_that_was_not_claimed_is_left_alone()
    {
        var rig = new Rig(notes: 3, started: false);

        await rig.Runner.RunAsync(rig.Run.Id);

        rig.Run.State.Should().Be(EvalRunState.Queued);
        await rig.Evaluations.DidNotReceive().GetCasesAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}