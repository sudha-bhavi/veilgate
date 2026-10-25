using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class MfaFactorRepositoryTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

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

    // One tenant and one active user, created as superuser.
    private async Task<(Tenant t, User u)> SeedUser()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        var t = Tenant.Create($"Acme {s}").Value;
        var u = User.Invite(t.Id, $"alice-{s}@acme.test", "Alice", UserRole.TenantAdmin, Now).Value;

        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.Add(u);
        await db.SaveChangesAsync();
        return (t, u);
    }

    private static MfaFactor NewFactor(User u, string secret = "v1:secret") =>
        MfaFactor.EnrollTotp(u.TenantId, u.Id, secret, Now).Value;

    [Fact]
    public async Task Added_factor_can_be_loaded_again()
    {
        var (t, u) = await SeedUser();
        var factor = NewFactor(u);

        await using (var db = AppContext(t.Id))
        {
            var repo = new MfaFactorRepository(db);
            repo.Add(factor);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(t.Id))
        {
            var loaded = await new MfaFactorRepository(db).GetTotpForUserAsync(u.Id, default);

            loaded.Should().NotBeNull();
            loaded!.Id.Should().Be(factor.Id);
            loaded.ProtectedSecret.Should().Be("v1:secret");
            loaded.IsConfirmed.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Returns_null_when_the_user_has_no_factor()
    {
        var (t, u) = await SeedUser();

        await using var db = AppContext(t.Id);

        (await new MfaFactorRepository(db).GetTotpForUserAsync(u.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Another_tenant_cannot_load_the_factor()
    {
        var (t, u) = await SeedUser();
        await using (var admin = pg.AdminContext())
        {
            admin.Add(NewFactor(u));
            await admin.SaveChangesAsync();
        }
        var (otherTenant, _) = await SeedUser();

        await using var db = AppContext(otherTenant.Id);

        (await new MfaFactorRepository(db).GetTotpForUserAsync(u.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_then_add_in_two_saves_replaces_the_factor()
    {
        var (t, u) = await SeedUser();
        var first = NewFactor(u, "v1:first");
        var second = NewFactor(u, "v1:second");

        await using (var db = AppContext(t.Id))
        {
            var repo = new MfaFactorRepository(db);
            repo.Add(first);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(t.Id))
        {
            var repo = new MfaFactorRepository(db);
            var existing = await repo.GetTotpForUserAsync(u.Id, default);
            repo.Remove(existing!);
            await repo.SaveChangesAsync(default);   // delete first...
            repo.Add(second);
            await repo.SaveChangesAsync(default);   // ...then insert
        }

        await using (var db = AppContext(t.Id))
        {
            var loaded = await new MfaFactorRepository(db).GetTotpForUserAsync(u.Id, default);
            loaded!.Id.Should().Be(second.Id);
        }
    }

    [Fact]
    public async Task A_user_cannot_have_two_totp_factors()
    {
        var (t, u) = await SeedUser();

        await using var db = AppContext(t.Id);
        var repo = new MfaFactorRepository(db);
        repo.Add(NewFactor(u, "v1:one"));
        repo.Add(NewFactor(u, "v1:two"));

        var act = () => repo.SaveChangesAsync(default);
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}