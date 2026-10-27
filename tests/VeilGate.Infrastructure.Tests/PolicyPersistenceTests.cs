using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Domain.Policies;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;
using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class PolicyPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

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

    private async Task<(Guid tenantId, Guid userId)> Seed()
    {
        var t = Tenant.Create($"Pol {Guid.NewGuid():N}"[..20]).Value;
        var u = User.Invite(t.Id, $"author-{Guid.NewGuid():N}@acme.test", "Author", UserRole.SecurityOfficer, Now).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        db.Add(u);
        await db.SaveChangesAsync();
        return (t.Id, u.Id);
    }

    [Fact]
    public async Task A_policy_with_a_draft_survives_a_round_trip()
    {
        var (tenantId, userId) = await Seed();
        var policy = Policy.Create(tenantId, "Default", "first policy").Value;
        var draft = policy.NewDraft("version: 1\nname: Default\n", userId).Value;

        await using (var db = AppContext(tenantId))
        {
            db.Policies.Add(policy);
            db.PolicyVersions.Add(draft);
            await db.SaveChangesAsync();   // pointer FKs are deferred, so this order does not matter
        }

        await using var read = AppContext(tenantId);
        var loadedPolicy = await read.Policies.AsNoTracking().SingleAsync(p => p.Id == policy.Id);
        var loadedVersion = await read.PolicyVersions.AsNoTracking().SingleAsync(v => v.Id == draft.Id);

        loadedPolicy.DraftVersionId.Should().Be(draft.Id);
        loadedPolicy.LastVersionNo.Should().Be(1);
        loadedVersion.State.Should().Be(PolicyState.Draft);
        loadedVersion.DefinitionSha256.Should().Be(PolicyVersion.Hash("version: 1\nname: Default\n"));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_policy()
    {
        var (a, userA) = await Seed();
        var (b, _) = await Seed();
        var policy = Policy.Create(a, "Secret", null).Value;
        var draft = policy.NewDraft("a: 1", userA).Value;
        await using (var db = AppContext(a))
        {
            db.Policies.Add(policy);
            db.PolicyVersions.Add(draft);
            await db.SaveChangesAsync();
        }

        await using var other = AppContext(b);

        (await other.Policies.CountAsync(p => p.Id == policy.Id)).Should().Be(0);
        (await other.PolicyVersions.CountAsync(v => v.Id == draft.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_pointer_to_a_version_that_does_not_exist_is_refused()
    {
        var (tenantId, _) = await Seed();
        var policy = Policy.Create(tenantId, "Pointers", null).Value;
        await using var db = AppContext(tenantId);
        db.Policies.Add(policy);
        await db.SaveChangesAsync();

        var act = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"update policies set current_version_id = {Guid.NewGuid()} where id = {policy.Id}");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_database_refuses_a_version_approved_by_its_submitter()
    {
        var (tenantId, userId) = await Seed();
        var policy = Policy.Create(tenantId, "Backstop", null).Value;
        var draft = policy.NewDraft("a: 1", userId).Value;
        await using var db = AppContext(tenantId);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(draft);
        await db.SaveChangesAsync();

        var act = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"update policy_versions set submitted_by = {userId}, approved_by = {userId} where id = {draft.Id}");

        await act.Should().ThrowAsync<Exception>();
    }

        [Fact]
    public async Task The_repository_finds_a_policy_and_its_version_for_its_own_tenant_only()
    {
        var (a, userA) = await Seed();
        var (b, _) = await Seed();
        var policy = Policy.Create(a, "Via repo", null).Value;
        var draft = policy.NewDraft("a: 1", userA).Value;
        await using (var db = AppContext(a))
        {
            IPolicyRepository repo = new PolicyRepository(db);
            repo.AddPolicy(policy);
            repo.AddVersion(draft);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(a))
        {
            IPolicyRepository repo = new PolicyRepository(db);
            (await repo.FindAsync(policy.Id, default)).Should().NotBeNull();
            (await repo.FindVersionAsync(draft.Id, default)).Should().NotBeNull();
        }

        await using (var db = AppContext(b))
        {
            IPolicyRepository repo = new PolicyRepository(db);
            (await repo.FindAsync(policy.Id, default)).Should().BeNull();
            (await repo.FindVersionAsync(draft.Id, default)).Should().BeNull();
        }
    }
}