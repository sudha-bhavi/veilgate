using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Policies;
using VeilGate.Domain;
using VeilGate.Domain.Policies;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>The list query on real PostgreSQL: the status filter, the search, and paging.</summary>
[Collection("postgres")]
public class PolicyRepositoryListTests(PostgresFixture pg)
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

    // A tenant with four policies in four different states.
    private async Task<Guid> SeedFourPolicies()
    {
        var t = Tenant.Create($"List {Guid.NewGuid():N}"[..20]).Value;
        var author = User.Invite(t.Id, $"author-{Guid.NewGuid():N}@acme.test", "Author", UserRole.SecurityOfficer, Now).Value;
        var approver = User.Invite(t.Id, $"approver-{Guid.NewGuid():N}@acme.test", "Approver", UserRole.SecurityOfficer, Now).Value;
        await using (var admin = pg.AdminContext())
        {
            admin.AddRange(t, author, approver);
            await admin.SaveChangesAsync();
        }

        var alpha = Policy.Create(t.Id, "Alpha research", null).Value;            // never published: Draft
        var alphaV = alpha.NewDraft("a: 1", author.Id).Value;

        var beta = Policy.Create(t.Id, "Beta published", null).Value;             // live: Published
        var betaV = beta.NewDraft("a: 1", author.Id).Value;
        betaV.Submit(author.Id, "Initial version", Now);
        beta.Publish(betaV, null, approver.Id, Now).IsSuccess.Should().BeTrue();

        var gamma = Policy.Create(t.Id, "Gamma pending", null).Value;            // waiting for review: PendingApproval
        var gammaV = gamma.NewDraft("a: 1", author.Id).Value;
        gammaV.Submit(author.Id, "Initial version", Now);

        var delta = Policy.Create(t.Id, "Delta archived", null).Value;           // Archived
        var deltaV = delta.NewDraft("a: 1", author.Id).Value;
        delta.Archive(Now);

        await using var db = AppContext(t.Id);
        db.Policies.AddRange(alpha, beta, gamma, delta);
        db.PolicyVersions.AddRange(alphaV, betaV, gammaV, deltaV);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private async Task<PolicyPage> List(Guid tenantId, string? status = null, string? search = null, int page = 1, int pageSize = 25)
    {
        await using var db = AppContext(tenantId);
        return await new PolicyRepository(db).ListAsync(status, search, page, pageSize, default);
    }

    [Fact]
    public async Task Lists_every_policy_by_name_with_its_status()
    {
        var tenantId = await SeedFourPolicies();

        var page = await List(tenantId);

        page.Total.Should().Be(4);
        page.Items.Select(i => (i.Name, i.Status)).Should().Equal(
            ("Alpha research", PolicyStatus.Draft),
            ("Beta published", PolicyStatus.Published),
            ("Delta archived", PolicyStatus.Archived),
            ("Gamma pending", PolicyStatus.PendingApproval));
        page.Items.Single(i => i.Name == "Beta published").CurrentVersion.Should().Be(1);
        page.Items.Single(i => i.Name == "Alpha research").CurrentVersion.Should().BeNull();
    }

    [Theory]
    [InlineData(PolicyStatus.Draft, "Alpha research")]
    [InlineData(PolicyStatus.Published, "Beta published")]
    [InlineData(PolicyStatus.Archived, "Delta archived")]
    [InlineData(PolicyStatus.PendingApproval, "Gamma pending")]
    public async Task The_status_filter_returns_only_matching_policies(string status, string expectedName)
    {
        var tenantId = await SeedFourPolicies();

        var page = await List(tenantId, status: status);

        page.Items.Select(i => i.Name).Should().Equal(expectedName);
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task Search_ignores_case_and_treats_wildcards_as_plain_text()
    {
        var tenantId = await SeedFourPolicies();

        (await List(tenantId, search: "RESEARCH")).Items.Select(i => i.Name).Should().Equal("Alpha research");
        (await List(tenantId, search: "%")).Total.Should().Be(0);
        (await List(tenantId, search: "_")).Total.Should().Be(0);
    }

    [Fact]
    public async Task Paging_returns_the_requested_slice_and_the_full_total()
    {
        var tenantId = await SeedFourPolicies();

        var second = await List(tenantId, page: 2, pageSize: 2);

        second.Total.Should().Be(4);
        second.Items.Select(i => i.Name).Should().Equal("Delta archived", "Gamma pending");
    }

    [Fact]
    public async Task Another_tenant_lists_nothing()
    {
        await SeedFourPolicies();
        var other = Tenant.Create($"Other {Guid.NewGuid():N}"[..20]).Value;
        await using (var admin = pg.AdminContext())
        {
            admin.Add(other);
            await admin.SaveChangesAsync();
        }

        (await List(other.Id)).Total.Should().Be(0);
    }

    [Fact]
    public async Task Versions_are_listed_newest_first()
    {
        var tenantId = await SeedFourPolicies();
        await using var db = AppContext(tenantId);
        var beta = await db.Policies.SingleAsync(p => p.Name == "Beta published");
        var author = await db.PolicyVersions.Where(v => v.PolicyId == beta.Id).Select(v => v.CreatedBy).FirstAsync();
        var second = beta.NewDraft("a: 2", author).Value;
        db.PolicyVersions.Add(second);
        await db.SaveChangesAsync();

        var (items, total) = await new PolicyRepository(db).ListVersionsAsync(beta.Id, 1, 25, default);

        total.Should().Be(2);
        items.Select(v => v.VersionNo).Should().Equal(2, 1);
    }
}
