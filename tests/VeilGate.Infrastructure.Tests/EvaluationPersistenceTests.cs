using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Evaluation;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;
using VeilGate.Application.Evaluation;
using System.Text.Json.Nodes;

namespace VeilGate.Infrastructure.Tests;

/// <summary>Datasets, runs and leaks on real PostgreSQL: isolation between tenants, the JSON labels, and the worker's queue claim.</summary>
[Collection("postgres")]
public class EvaluationPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A context for a tenant, or for the worker before it knows one (null).</summary>
    private VeilGateDbContext AppContext(Guid? tenantId)
    {
        var tenant = new TenantContext();
        if (tenantId is { } id) tenant.Set(id);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Eval {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private async Task<EvalDataset> SeedDataset(Guid tenantId, int notes = 2)
    {
        await using var db = AppContext(tenantId);
        var repo = new EvaluationRepository(db);
        var dataset = EvalDataset.Create(tenantId, $"ds-{Guid.NewGuid():N}"[..12], notes).Value;
        var cases = Enumerable.Range(0, notes)
            .Select(i => EvalCase.Create(tenantId, dataset.Id, i, $"Patient SSN 123-45-6789 note {i}", [new("SSN", 12, 23)]).Value);
        repo.AddDataset(dataset, cases);
        await repo.SaveChangesAsync(default);
        return dataset;
    }

    private async Task<EvalRun> QueueRun(Guid tenantId, Guid datasetId, int total = 2)
    {
        await using var db = AppContext(tenantId);
        var repo = new EvaluationRepository(db);
        var run = EvalRun.Queue(tenantId, datasetId, Guid.NewGuid(), 1, EvalRun.Modes.Rules, 0.98, Guid.NewGuid(), total).Value;
        repo.AddRun(run);
        await repo.SaveChangesAsync(default);
        return run;
    }

    private async Task<ClaimedRun?> Claim(int staleSeconds = 600, int maxAttempts = 3)
    {
        await using var db = AppContext(null);
        return await new EvaluationRepository(db).ClaimNextRunAsync(TimeSpan.FromSeconds(staleSeconds), maxAttempts, default);
    }

    /// <summary>The queue is shared by every test in the collection, so a claim test empties it first.</summary>
    private async Task Drain()
    {
        while (await Claim() is not null) { }
    }

    private async Task<EvalRun> Load(Guid id)
    {
        await using var admin = pg.AdminContext();
        return await admin.EvalRuns.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    // ------------------------------------------------------------ datasets

    [Fact]
    public async Task A_dataset_and_its_labelled_notes_round_trip()
    {
        var tenantId = await SeedTenant();
        var dataset = await SeedDataset(tenantId, notes: 3);

        await using var db = AppContext(tenantId);
        var repo = new EvaluationRepository(db);

        (await repo.FindDatasetAsync(dataset.Id, default))!.CaseCount.Should().Be(3);
        var notes = await repo.GetCasesAsync(dataset.Id, -1, 10, default);
        notes.Select(n => n.Ordinal).Should().Equal(0, 1, 2);
        notes[0].Expected.Should().ContainSingle().Which.Should().Be(new LabeledSpan("SSN", 12, 23));

        (await repo.GetCasesAsync(dataset.Id, 0, 10, default)).Select(n => n.Ordinal).Should().Equal(1, 2);   // paging resumes after an ordinal
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_it()
    {
        var owner = await SeedTenant();
        var other = await SeedTenant();
        var dataset = await SeedDataset(owner);
        var run = await QueueRun(owner, dataset.Id);

        await using var db = AppContext(other);
        var repo = new EvaluationRepository(db);

        (await repo.FindDatasetAsync(dataset.Id, default)).Should().BeNull();
        (await repo.ListDatasetsAsync(default)).Should().BeEmpty();
        (await repo.GetCasesAsync(dataset.Id, -1, 10, default)).Should().BeEmpty();
        (await repo.FindRunAsync(run.Id, default)).Should().BeNull();
        (await repo.ListRunsAsync(0, 10, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_dataset_name_is_unique_within_a_tenant()
    {
        var tenantId = await SeedTenant();
        var dataset = await SeedDataset(tenantId);

        await using var db = AppContext(tenantId);
        (await new EvaluationRepository(db).DatasetNameExistsAsync(dataset.Name, default)).Should().BeTrue();

        db.EvalDatasets.Add(EvalDataset.Create(tenantId, dataset.Name, 1).Value);
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    // ------------------------------------------------------------ runs

    [Fact]
    public async Task A_queued_run_has_no_result_and_a_finished_one_keeps_it()
    {
        var tenantId = await SeedTenant();
        var dataset = await SeedDataset(tenantId);
        var queued = await QueueRun(tenantId, dataset.Id);
        await Drain();   // the claim marks it running, as the worker would

        var summary = new EvalRunResult(0.99, 0.97, 0.98, 0, 0, true, """{"SSN":{"tp":2,"fp":0,"fn":0}}""");
        await using (var db = AppContext(tenantId))
        {
            var repo = new EvaluationRepository(db);
            var run = (await repo.FindRunAsync(queued.Id, default))!;
            run.State.Should().Be(EvalRunState.Running);
            run.Summary.Should().BeNull();

            run.ReportProgress(1).IsSuccess.Should().BeTrue();
            run.Complete(summary, Now).IsSuccess.Should().BeTrue();
            await repo.SaveChangesAsync(default);
        }

        var saved = await Load(queued.Id);
        saved.State.Should().Be(EvalRunState.Succeeded);
        saved.Summary.Should().BeEquivalentTo(summary, o => o.Excluding(s => s.PerTypeJson));
        JsonNode.DeepEquals(JsonNode.Parse(saved.Summary!.PerTypeJson), JsonNode.Parse(summary.PerTypeJson)).Should().BeTrue();   // jsonb re-spaces and re-orders keys
        saved.FinishedAt.Should().Be(Now);
        saved.DoneCases.Should().Be(saved.TotalCases);
    }

    [Fact]
    public async Task Active_runs_are_counted_per_tenant_and_newest_comes_first()
    {
        var tenantId = await SeedTenant();
        var dataset = await SeedDataset(tenantId);
        var first = await QueueRun(tenantId, dataset.Id);
        var second = await QueueRun(tenantId, dataset.Id);

        await using var db = AppContext(tenantId);
        var repo = new EvaluationRepository(db);

        (await repo.CountActiveRunsAsync(default)).Should().Be(2);
        (await repo.ListRunsAsync(0, 10, default)).Select(r => r.Id).Should().Equal(second.Id, first.Id);
    }

    // ------------------------------------------------------------ the queue claim

    [Fact]
    public async Task The_oldest_queued_run_is_claimed_once_and_its_tenant_comes_back()
    {
        await Drain();
        var tenantA = await SeedTenant();
        var tenantB = await SeedTenant();
        var older = await QueueRun(tenantA, (await SeedDataset(tenantA)).Id);
        var newer = await QueueRun(tenantB, (await SeedDataset(tenantB)).Id);

        var first = await Claim();
        var second = await Claim();
        var third = await Claim();

        first.Should().Be(new ClaimedRun(older.Id, tenantA));
        second.Should().Be(new ClaimedRun(newer.Id, tenantB));
        third.Should().BeNull();

        var claimed = await Load(older.Id);
        claimed.State.Should().Be(EvalRunState.Running);
        claimed.Attempts.Should().Be(1);
        claimed.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Two_workers_never_get_the_same_run()
    {
        await Drain();
        var tenantId = await SeedTenant();
        var dataset = await SeedDataset(tenantId);
        for (var i = 0; i < 6; i++) await QueueRun(tenantId, dataset.Id);

        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Claim()));

        var taken = claims.Where(c => c is not null).Select(c => c!.RunId).ToList();
        taken.Should().HaveCount(6);
        taken.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_run_abandoned_by_a_crashed_worker_goes_back_in_the_queue()
    {
        await Drain();
        var tenantId = await SeedTenant();
        var run = await QueueRun(tenantId, (await SeedDataset(tenantId)).Id);
        (await Claim())!.RunId.Should().Be(run.Id);

        await using (var admin = pg.AdminContext())
            await admin.Database.ExecuteSqlInterpolatedAsync($"UPDATE eval_runs SET started_at = now() - interval '1 hour' WHERE id = {run.Id}");

        var again = await Claim(staleSeconds: 60, maxAttempts: 3);

        again!.RunId.Should().Be(run.Id);
        (await Load(run.Id)).Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_run_that_has_used_all_its_attempts_fails_instead_of_looping_forever()
    {
        await Drain();
        var tenantId = await SeedTenant();
        var run = await QueueRun(tenantId, (await SeedDataset(tenantId)).Id);
        await using (var admin = pg.AdminContext())
            await admin.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE eval_runs SET state = 'Running', attempts = 3, started_at = now() - interval '1 hour' WHERE id = {run.Id}");

        (await Claim(staleSeconds: 60, maxAttempts: 3)).Should().BeNull();

        var failed = await Load(run.Id);
        failed.State.Should().Be(EvalRunState.Failed);
        failed.ErrorCode.Should().Be("evaluation.abandoned");
    }

    [Fact]
    public async Task A_run_that_is_still_fresh_is_left_alone()
    {
        await Drain();
        var tenantId = await SeedTenant();
        var run = await QueueRun(tenantId, (await SeedDataset(tenantId)).Id);
        (await Claim()).Should().NotBeNull();

        (await Claim(staleSeconds: 600)).Should().BeNull();
        (await Load(run.Id)).Attempts.Should().Be(1);
    }

    // ------------------------------------------------------------ leaks

    [Fact]
    public async Task Leaks_are_stored_per_run_in_order_and_hidden_from_other_tenants()
    {
        var tenantId = await SeedTenant();
        var other = await SeedTenant();
        var dataset = await SeedDataset(tenantId);
        var run = await QueueRun(tenantId, dataset.Id);
        var caseId = Guid.NewGuid();

        await using (var db = AppContext(tenantId))
        {
            var repo = new EvaluationRepository(db);
            repo.AddLeaks([
                EvalLeak.Create(tenantId, run.Id, caseId, "SSN", 12, 23, LeakReason.NotDetected),
                EvalLeak.Create(tenantId, run.Id, caseId, "PHONE", 30, 42, LeakReason.PartlyProtected)]);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(tenantId))
        {
            var leaks = await new EvaluationRepository(db).ListLeaksAsync(run.Id, 0, 10, default);
            leaks.Select(l => l.EntityType).Should().Equal("SSN", "PHONE");
            leaks[1].Reason.Should().Be("partly_protected");
            (await new EvaluationRepository(db).ListLeaksAsync(run.Id, 1, 10, default)).Should().ContainSingle();
        }

        await using var otherDb = AppContext(other);
        (await new EvaluationRepository(otherDb).ListLeaksAsync(run.Id, 0, 10, default)).Should().BeEmpty();
    }
}