using FluentAssertions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Evaluation;
using Xunit;

namespace VeilGate.Domain.Tests;

public class EvaluationDomainTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static EvalRun NewRun(double threshold = 0.98) =>
        EvalRun.Queue(Tenant, Guid.NewGuid(), Guid.NewGuid(), 1, EvalRun.Modes.Rules, threshold, Guid.NewGuid(), 10).Value;

    private static EvalRunResult Good() => new(1, 1, 1, 0, 0, true, "{}");

    // ------------------------------------------------------------ dataset and cases

    [Theory]
    [InlineData("", 10)]
    [InlineData("  ", 10)]
    [InlineData("ok", 0)]
    [InlineData("ok", EvalDataset.MaxCases + 1)]
    public void A_dataset_needs_a_name_and_a_sensible_size(string name, int cases)
        => EvalDataset.Create(Tenant, name, cases).Error.Kind.Should().Be(ErrorKind.Validation);

    [Fact]
    public void A_valid_dataset_trims_its_name()
        => EvalDataset.Create(Tenant, "  standard  ", 100).Value.Name.Should().Be("standard");

    [Fact]
    public void A_case_keeps_its_text_and_labels()
    {
        var item = EvalCase.Create(Tenant, Guid.NewGuid(), 0, "SSN 123-45-6789", [new("SSN", 4, 15)]).Value;

        item.Text.Should().Be("SSN 123-45-6789");
        item.Expected.Should().ContainSingle().Which.Should().Be(new LabeledSpan("SSN", 4, 15));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 4)]
    [InlineData(0, 99)]
    public void A_label_outside_the_note_is_refused(int start, int end)
        => EvalCase.Create(Tenant, Guid.NewGuid(), 0, "short note", [new("SSN", start, end)]).Error.Kind.Should().Be(ErrorKind.Validation);

    // ------------------------------------------------------------ run life cycle

    [Fact]
    public void A_new_run_is_queued_with_no_result()
    {
        var run = NewRun();

        run.State.Should().Be(EvalRunState.Queued);
        run.Summary.Should().BeNull();
        run.Attempts.Should().Be(0);
    }

    [Theory]
    [InlineData("deep")]
    [InlineData("")]
    public void An_unknown_mode_is_refused(string mode)
        => EvalRun.Queue(Tenant, Guid.NewGuid(), Guid.NewGuid(), 1, mode, 0.98, Guid.NewGuid(), 10).Error.Kind.Should().Be(ErrorKind.Validation);

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void A_threshold_outside_zero_to_one_is_refused(double threshold)
        => EvalRun.Queue(Tenant, Guid.NewGuid(), Guid.NewGuid(), 1, "rules", threshold, Guid.NewGuid(), 10).IsFailure.Should().BeTrue();

    [Fact]
    public void A_queued_run_can_start_once()
    {
        var run = NewRun();

        run.Start(Now).IsSuccess.Should().BeTrue();
        run.State.Should().Be(EvalRunState.Running);
        run.Attempts.Should().Be(1);
        run.StartedAt.Should().Be(Now);
        run.Start(Now).Error.Code.Should().Be("evaluation.not_queued");
    }

    [Fact]
    public void Progress_moves_forward_only_and_stops_at_the_total()
    {
        var run = NewRun();
        run.Start(Now);

        run.ReportProgress(4);
        run.ReportProgress(2);
        run.DoneCases.Should().Be(4);
        run.ReportProgress(999);
        run.DoneCases.Should().Be(10);
    }

    [Fact]
    public void Progress_cannot_be_reported_before_the_run_starts()
        => NewRun().ReportProgress(1).Error.Code.Should().Be("evaluation.not_running");

    [Fact]
    public void A_running_run_completes_with_its_result()
    {
        var run = NewRun();
        run.Start(Now);

        run.Complete(Good(), Now.AddMinutes(1)).IsSuccess.Should().BeTrue();

        run.State.Should().Be(EvalRunState.Succeeded);
        run.DoneCases.Should().Be(10);
        run.Summary.Should().Be(Good());
        run.FinishedAt.Should().Be(Now.AddMinutes(1));
        run.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void A_run_that_was_never_started_cannot_complete()
        => NewRun().Complete(Good(), Now).Error.Code.Should().Be("evaluation.not_running");

    [Fact]
    public void A_queued_or_running_run_can_fail_and_records_only_the_code()
    {
        var queued = NewRun();
        var running = NewRun();
        running.Start(Now);

        queued.Fail("policy.missing", Now).IsSuccess.Should().BeTrue();
        running.Fail(new string('x', 500), Now).IsSuccess.Should().BeTrue();

        queued.State.Should().Be(EvalRunState.Failed);
        queued.ErrorCode.Should().Be("policy.missing");
        running.ErrorCode!.Length.Should().Be(EvalRun.MaxErrorLength);
    }

    [Fact]
    public void A_finished_run_never_changes_again()
    {
        var done = NewRun();
        done.Start(Now);
        done.Complete(Good(), Now);
        var failed = NewRun();
        failed.Fail("x", Now);

        done.Fail("late", Now).Error.Code.Should().Be("evaluation.finished");
        failed.Fail("again", Now).Error.Code.Should().Be("evaluation.finished");
        failed.Start(Now).IsFailure.Should().BeTrue();
        done.Complete(Good(), Now).IsFailure.Should().BeTrue();
        done.State.Should().Be(EvalRunState.Succeeded);
    }
}