using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Evaluation;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Domain.Evaluation;
using Xunit;

namespace VeilGate.Domain.Tests;

public class DatasetHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly EvaluationActor Actor = new(Guid.NewGuid(), "203.0.113.5");

    private sealed class Setup
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public IEvaluationRepository Repo { get; } = Substitute.For<IEvaluationRepository>();
        public IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();
        public EvalDataset? Added { get; private set; }
        public List<EvalCase> AddedCases { get; private set; } = [];
        public CreateSyntheticDatasetHandler Handler { get; }

        public Setup(bool nameTaken = false, int existingDatasets = 0)
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(TenantId);
            tenant.IsSet.Returns(true);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);

            Repo.DatasetNameExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(nameTaken);
            Repo.ListDatasetsAsync(Arg.Any<CancellationToken>())
                .Returns(Enumerable.Range(0, existingDatasets).Select(i => EvalDataset.Create(TenantId, $"d{i}", 1).Value).ToList());
            Repo.When(r => r.AddDataset(Arg.Any<EvalDataset>(), Arg.Any<IEnumerable<EvalCase>>())).Do(ci =>
            {
                Added = ci.ArgAt<EvalDataset>(0);
                AddedCases = ci.ArgAt<IEnumerable<EvalCase>>(1).ToList();
            });

            Handler = new CreateSyntheticDatasetHandler(Repo, tenant,
                new EvaluationAuditor(Audit, tenant, clock, NullLogger<EvaluationAuditor>.Instance));
        }
    }

    [Fact]
    public async Task It_creates_the_dataset_with_numbered_notes_for_the_callers_tenant()
    {
        var s = new Setup();

        var result = await s.Handler.HandleAsync(new CreateSyntheticDatasetCommand("standard", Count: 25, Seed: null, Actor));

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("standard");
        result.Value.CaseCount.Should().Be(25);
        s.Added!.TenantId.Should().Be(s.TenantId);
        s.AddedCases.Select(c => c.Ordinal).Should().Equal(Enumerable.Range(0, 25));
        s.AddedCases.Should().OnlyContain(c => c.TenantId == s.TenantId && c.DatasetId == s.Added.Id);
        await s.Repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await s.Audit.Received(1).WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_count_it_makes_the_default_number_of_notes()
    {
        var s = new Setup();

        (await s.Handler.HandleAsync(new CreateSyntheticDatasetCommand("standard", null, null, Actor))).Value.CaseCount
            .Should().Be(CreateSyntheticDatasetHandler.DefaultCount);
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_notes_and_another_seed_gives_other_notes()
    {
        var a = new Setup();
        var b = new Setup();
        var c = new Setup();

        await a.Handler.HandleAsync(new CreateSyntheticDatasetCommand("x", 20, 5, Actor));
        await b.Handler.HandleAsync(new CreateSyntheticDatasetCommand("x", 20, 5, Actor));
        await c.Handler.HandleAsync(new CreateSyntheticDatasetCommand("x", 20, 6, Actor));

        a.AddedCases.Select(n => n.Text).Should().Equal(b.AddedCases.Select(n => n.Text));
        a.AddedCases.Select(n => n.Text).Should().NotEqual(c.AddedCases.Select(n => n.Text));
    }

    [Fact]
    public async Task A_name_already_in_use_is_a_conflict_and_nothing_is_stored()
    {
        var s = new Setup(nameTaken: true);

        var result = await s.Handler.HandleAsync(new CreateSyntheticDatasetCommand("standard", 10, null, Actor));

        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        s.Repo.DidNotReceive().AddDataset(Arg.Any<EvalDataset>(), Arg.Any<IEnumerable<EvalCase>>());
        await s.Audit.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_tenant_at_the_dataset_limit_cannot_add_another()
    {
        var s = new Setup(existingDatasets: CreateSyntheticDatasetHandler.MaxDatasetsPerTenant);

        var result = await s.Handler.HandleAsync(new CreateSyntheticDatasetCommand("one-more", 10, null, Actor));

        result.Error.Code.Should().Be("evaluation.dataset_limit");
        s.Added.Should().BeNull();
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("   ", 10)]
    [InlineData("ok", 0)]
    [InlineData("ok", EvalDataset.MaxCases + 1)]
    public async Task A_bad_name_or_size_is_refused_before_anything_is_generated(string name, int count)
    {
        var s = new Setup();

        var result = await s.Handler.HandleAsync(new CreateSyntheticDatasetCommand(name, count, null, Actor));

        result.Error.Kind.Should().Be(ErrorKind.Validation);
        s.Added.Should().BeNull();
    }

    [Fact]
    public async Task Getting_a_dataset_that_is_not_there_is_not_found()
    {
        var repo = Substitute.For<IEvaluationRepository>();
        repo.FindDatasetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((EvalDataset?)null);

        (await new GetDatasetHandler(repo).HandleAsync(Guid.NewGuid())).Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}