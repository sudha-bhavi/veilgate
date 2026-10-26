using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class SessionRepositoryTests(PostgresFixture pg)
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

    private static string NewHash() => RefreshTokens.Hash(RefreshTokens.Generate());

    private async Task<Session> SaveSession(Tenant t, User u, string hash)
    {
        var session = Session.Start(t.Id, u.Id, hash, Now).Value;
        await using var db = AppContext(t.Id);
        var repo = new SessionRepository(db);
        repo.Add(session);
        await repo.SaveChangesAsync(default);
        return session;
    }

    [Fact]
    public async Task A_saved_session_is_found_by_its_current_hash()
    {
        var (t, u) = await SeedUser();
        var hash = NewHash();
        var saved = await SaveSession(t, u, hash);

        await using var db = AppContext(t.Id);
        var found = await new SessionRepository(db).FindByRefreshHashAsync(hash, default);

        found.Should().NotBeNull();
        found!.Id.Should().Be(saved.Id);
    }

    [Fact]
    public async Task After_rotation_the_old_hash_still_finds_the_session_so_reuse_can_be_caught()
    {
        var (t, u) = await SeedUser();
        var first = NewHash();
        await SaveSession(t, u, first);

        await using (var db = AppContext(t.Id))
        {
            var repo = new SessionRepository(db);
            var s = await repo.FindByRefreshHashAsync(first, default);
            s!.Rotate(first, NewHash(), Now.AddMinutes(1)).IsSuccess.Should().BeTrue();
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(t.Id))
        {
            var s = await new SessionRepository(db).FindByRefreshHashAsync(first, default);
            s.Should().NotBeNull();
            s!.PreviousRefreshHash.Should().Be(first);
        }
    }

    [Fact]
    public async Task A_revoked_session_is_not_listed_as_active()
    {
        var (t, u) = await SeedUser();
        var keep = await SaveSession(t, u, NewHash());
        var hashToRevoke = NewHash();
        await SaveSession(t, u, hashToRevoke);

        await using (var db = AppContext(t.Id))
        {
            var repo = new SessionRepository(db);
            (await repo.FindByRefreshHashAsync(hashToRevoke, default))!.Revoke("logout", Now);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(t.Id))
        {
            var active = await new SessionRepository(db).ListActiveForUserAsync(u.Id, Now.AddMinutes(1), default);
            active.Select(s => s.Id).Should().ContainSingle().Which.Should().Be(keep.Id);
        }
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_session()
    {
        var (t, u) = await SeedUser();
        var hash = NewHash();
        await SaveSession(t, u, hash);
        var (other, _) = await SeedUser();

        await using var db = AppContext(other.Id);

        (await new SessionRepository(db).FindByRefreshHashAsync(hash, default)).Should().BeNull();
    }
        // A context with NO tenant set, like a refresh request before we know who it is.
    private VeilGateDbContext NoTenantContext() =>
        new(new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(new TenantContext()))
            .Options);

    [Fact]
    public async Task Without_a_tenant_the_lookup_still_finds_the_owner_of_a_current_or_old_token()
    {
        var (t, u) = await SeedUser();
        var first = NewHash();
        await SaveSession(t, u, first);
        var second = NewHash();
        await using (var db = AppContext(t.Id))
        {
            var repo = new SessionRepository(db);
            (await repo.FindByRefreshHashAsync(first, default))!.Rotate(first, second, Now.AddMinutes(1));
            await repo.SaveChangesAsync(default);
        }

        await using var noTenant = NoTenantContext();
        var repoNoTenant = new SessionRepository(noTenant);

        (await repoNoTenant.FindTenantByRefreshHashAsync(second, default)).Should().Be(t.Id);   // current token
        (await repoNoTenant.FindTenantByRefreshHashAsync(first, default)).Should().Be(t.Id);    // old token, so reuse can be caught
        (await repoNoTenant.FindByRefreshHashAsync(second, default)).Should().BeNull();         // RLS still hides the row itself
    }

    [Fact]
    public async Task The_lookup_returns_nothing_for_an_unknown_token()
    {
        await using var db = NoTenantContext();

        (await new SessionRepository(db).FindTenantByRefreshHashAsync(NewHash(), default)).Should().BeNull();
    }
}