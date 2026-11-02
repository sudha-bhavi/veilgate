using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class UserRepositoryListTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private VeilGateDbContext AppContext(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        return new VeilGateDbContext(new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options);
    }

    private sealed record Seeded(Tenant Tenant, User Admin, User Ann, User Invited, MfaFactor AnnFactor);

    // Admin (active TenantAdmin), Ann Lee (active Developer with a confirmed authenticator), Zed Invited (not accepted yet).
    private async Task<Seeded> Seed()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        var t = Tenant.Create($"Acme {s}").Value;
        var admin = User.Invite(t.Id, $"admin-{s}@acme.test", "Admin", UserRole.TenantAdmin, Now).Value;
        admin.AcceptInvitation("h", Now);
        var ann = User.Invite(t.Id, $"ann-{s}@acme.test", "Ann Lee", UserRole.Developer, Now).Value;
        ann.AcceptInvitation("h", Now);
        var invited = User.Invite(t.Id, $"zed-{s}@acme.test", "Zed Invited", UserRole.Auditor, Now).Value;
        var factor = MfaFactor.EnrollTotp(t.Id, ann.Id, "v1:x", Now).Value;
        factor.Confirm(1, Now);

        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.AddRange(admin, ann, invited);
        await db.SaveChangesAsync();
        db.Add(factor);
        await db.SaveChangesAsync();
        return new Seeded(t, admin, ann, invited, factor);
    }

    [Fact]
    public async Task Listing_shows_only_this_tenants_people_by_name_with_the_mfa_flag()
    {
        var a = await Seed();
        await Seed();   // another tenant, which must stay invisible

        await using var db = AppContext(a.Tenant.Id);
        var (rows, total) = await new UserRepository(db).ListAsync(null, null, null, 1, 25, default);

        total.Should().Be(3);
        rows.Select(r => r.User.DisplayName).Should().Equal("Admin", "Ann Lee", "Zed Invited");
        rows.Single(r => r.User.Id == a.Ann.Id).HasMfa.Should().BeTrue();
        rows.Single(r => r.User.Id == a.Admin.Id).HasMfa.Should().BeFalse();
    }

    [Fact]
    public async Task Listing_filters_by_role_and_status()
    {
        var a = await Seed();

        await using var db = AppContext(a.Tenant.Id);
        var repo = new UserRepository(db);

        (await repo.ListAsync(UserRole.Developer, null, null, 1, 25, default)).Items.Single().User.Id.Should().Be(a.Ann.Id);
        (await repo.ListAsync(null, UserStatus.Invited, null, 1, 25, default)).Items.Single().User.Id.Should().Be(a.Invited.Id);
        (await repo.ListAsync(UserRole.Developer, UserStatus.Invited, null, 1, 25, default)).Total.Should().Be(0);
    }

    [Fact]
    public async Task Listing_searches_name_and_email_and_treats_wildcards_as_plain_text()
    {
        var a = await Seed();

        await using var db = AppContext(a.Tenant.Id);
        var repo = new UserRepository(db);

        (await repo.ListAsync(null, null, "ANN", 1, 25, default)).Items.Single().User.Id.Should().Be(a.Ann.Id);          // name, any case
        (await repo.ListAsync(null, null, "zed-", 1, 25, default)).Items.Single().User.Id.Should().Be(a.Invited.Id);     // email
        (await repo.ListAsync(null, null, "%", 1, 25, default)).Total.Should().Be(0);                                     // not "match everything"
    }

    [Fact]
    public async Task Listing_pages()
    {
        var a = await Seed();

        await using var db = AppContext(a.Tenant.Id);
        var (rows, total) = await new UserRepository(db).ListAsync(null, null, null, 2, 1, default);

        total.Should().Be(3);
        rows.Single().User.DisplayName.Should().Be("Ann Lee");
    }

    [Fact]
    public async Task Counting_admins_ignores_the_named_person_and_anyone_not_active()
    {
        var a = await Seed();
        await using (var admin = pg.AdminContext())
        {
            var second = User.Invite(a.Tenant.Id, $"second-{Guid.NewGuid():N}@acme.test", "Second", UserRole.TenantAdmin, Now).Value;   // still Invited
            admin.Add(second);
            await admin.SaveChangesAsync();
        }

        await using var db = AppContext(a.Tenant.Id);
        var repo = new UserRepository(db);

        (await repo.CountActiveAdminsExceptAsync(a.Admin.Id, default)).Should().Be(0);     // the invited admin does not count
        (await repo.CountActiveAdminsExceptAsync(Guid.NewGuid(), default)).Should().Be(1); // the active one does
    }

    [Fact]
    public async Task The_app_role_can_remove_a_factor_and_recovery_codes()
    {
        var a = await Seed();
        await using (var admin = pg.AdminContext())
        {
            admin.Add(RecoveryCode.Create(a.Tenant.Id, a.Ann.Id, RecoveryCodes.Hash(RecoveryCodes.Generate()[0]), Now).Value);
            await admin.SaveChangesAsync();
        }

        await using (var db = AppContext(a.Tenant.Id))
        {
            var factors = new MfaFactorRepository(db);
            var codes = new RecoveryCodeRepository(db);
            factors.Remove((await factors.GetTotpForUserAsync(a.Ann.Id, default))!);
            codes.RemoveRange(await codes.ListForUserAsync(a.Ann.Id, default));
            await db.SaveChangesAsync();
        }

        await using var check = AppContext(a.Tenant.Id);
        (await new MfaFactorRepository(check).GetTotpForUserAsync(a.Ann.Id, default)).Should().BeNull();
        (await new RecoveryCodeRepository(check).ListForUserAsync(a.Ann.Id, default)).Should().BeEmpty();
    }
}
