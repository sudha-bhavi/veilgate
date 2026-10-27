using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Evaluation;
using VeilGate.Application.Policies;
using VeilGate.Domain;
using VeilGate.Domain.Evaluation;
using VeilGate.Domain.Policies;
using VeilGate.Infrastructure.Detection;
using VeilGate.Infrastructure.Evaluation;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>An evaluation from the queue to a stored result: real claim, real detectors, real policy, real database.</summary>
[Collection("postgres")]
public class EvaluationRunnerIntegrationTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IClock>(new FixedClock());
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<VeilGateDbContext>((sp, o) => o
            .UseNpgsql(pg.AppConnectionForApi)
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        services.AddScoped<IEvaluationRepository, EvaluationRepository>();
        services.AddScoped<IPolicyRepository, PolicyRepository>();
        services.AddSingleton<IDetector, RegexDetector>();
        services.AddSingleton<IDetector, DictionaryDetector>();
        services.AddSingleton<IDetector, NameHeuristicDetector>();
        services.AddEvaluationRunner();
        return services.BuildServiceProvider();
    }

    private VeilGateDbContext AppContext(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    /// <summary>A tenant with a policy (a draft: any state can be evaluated) and a synthetic dataset.</summary>
    private async Task<(Guid TenantId, Guid PolicyId, int VersionNo, EvalDataset Dataset)> Seed(int notes = 60)
    {
        var tenant = Tenant.Create($"Run {Guid.NewGuid():N}"[..20]).Value;
        var user = User.Invite(tenant.Id, $"ev-{Guid.NewGuid():N}@acme.test", "Evaluator", UserRole.SecurityOfficer, Now).Value;
        await using (var admin = pg.AdminContext())
        {
            admin.Add(tenant);
            admin.Add(user);
            await admin.SaveChangesAsync();
        }

        var policy = Policy.Create(tenant.Id, "hipaa", null).Value;
        var version = policy.NewDraft(PolicyTemplates.YamlOf(PolicyTemplates.HipaaSafeHarbor), user.Id).Value;
        var dataset = EvalDataset.Create(tenant.Id, $"ds-{Guid.NewGuid():N}"[..12], notes).Value;
        var generated = SyntheticDatasetGenerator.Generate(notes, seed: 99);
        var cases = generated.Select((g, i) => EvalCase.Create(tenant.Id, dataset.Id, i, g.Text, g.Expected).Value);

        await using var db = AppContext(tenant.Id);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        var repo = new EvaluationRepository(db);
        repo.AddDataset(dataset, cases);
        await db.SaveChangesAsync();
        return (tenant.Id, policy.Id, version.VersionNo, dataset);
    }

    private async Task<EvalRun> Queue(Guid tenantId, Guid datasetId, Guid policyId, int versionNo, int total, string mode = EvalRun.Modes.RulesAndNer)
    {
        await using var db = AppContext(tenantId);
        var run = EvalRun.Queue(tenantId, datasetId, policyId, versionNo, mode, 0.98, Guid.NewGuid(), total).Value;
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Works through the queue until it is empty (other tests may have left runs in it).</summary>
    private static async Task ProcessAll(ServiceProvider services)
    {
        var processor = new EvaluationQueueProcessor(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EvaluationQueueProcessor>.Instance);
        while (await processor.ProcessNextAsync(Stale, 3, default)) { }
    }

    private async Task<EvalRun> Load(Guid id)
    {
        await using var admin = pg.AdminContext();
        return await admin.EvalRuns.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task A_queued_run_is_claimed_scored_and_stored_for_its_own_tenant()
    {
        await using var services = Services();
        var (tenantId, policyId, versionNo, dataset) = await Seed();
        var queued = await Queue(tenantId, dataset.Id, policyId, versionNo, dataset.CaseCount);

        await ProcessAll(services);

        var run = await Load(queued.Id);
        run.State.Should().Be(EvalRunState.Succeeded);
        run.DoneCases.Should().Be(dataset.CaseCount);
        run.Attempts.Should().Be(1);
        run.Summary.Should().NotBeNull();
        run.Summary!.Recall.Should().BeGreaterThan(0.5);   // loose on purpose: the exact figure is what the evaluation exists to show
        run.Summary.PerTypeJson.Should().Contain("SSN");

        await using var admin = pg.AdminContext();
        var leaks = await admin.EvalRunLeaks.AsNoTracking().Where(l => l.RunId == queued.Id).ToListAsync();
        leaks.Should().HaveCount(run.Summary.Leaks);
                leaks.All(l => l.TenantId == tenantId).Should().BeTrue();   // true for an empty list too: the unit tests prove leaks carry the right tenant
    }

    [Fact]
    public async Task A_run_whose_policy_is_missing_fails_cleanly_instead_of_crashing_the_worker()
    {
        await using var services = Services();
        var (tenantId, _, _, dataset) = await Seed(notes: 5);
        var queued = await Queue(tenantId, dataset.Id, Guid.NewGuid(), 1, dataset.CaseCount);

        await ProcessAll(services);

        var run = await Load(queued.Id);
        run.State.Should().Be(EvalRunState.Failed);
        run.ErrorCode.Should().Be("policy.not_found");
        run.Summary.Should().BeNull();
    }
}