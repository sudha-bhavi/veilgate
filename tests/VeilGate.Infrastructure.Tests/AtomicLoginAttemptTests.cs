using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>
/// Penetration test finding VG-PT-003 (PT-AUTH-04): 120 wrong codes sent at once were all checked and the account never locked,
/// because each request checked "is it locked?", checked the code, and only then added one to the failure count.
/// The fix counts the attempt first, in one database statement. These tests send many attempts at the same moment against a real
/// PostgreSQL and require that exactly five get through to be checked.
///
/// They use the normal connection pool on purpose (the shared test connection allows one connection at a time, which would hide the race).
/// </summary>
[Collection("postgres")]
public class AtomicLoginAttemptTests(PostgresFixture pg)
{
    private const int Racers = 20;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private VeilGateDbContext AppContext(Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnectionForApi)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    private async Task<(Tenant Tenant, User User)> SeedUser()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        var t = Tenant.Create($"Attempts {s}").Value;
        var u = User.Invite(t.Id, $"bob-{s}@acme.test", "Bob", UserRole.Developer, Now).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.Add(u);
        await db.SaveChangesAsync();
        return (t, u);
    }

    private async Task<bool> Reserve(Tenant t, Guid userId, DateTimeOffset when)
    {
        await using var db = AppContext(t.Id);
        var repo = new UserRepository(db);
        var user = (await repo.GetCurrentTenantUserAsync(userId, default))!;
        return await repo.TryReserveLoginAttemptAsync(user, when, default);
    }

    [Fact]
    public async Task Many_attempts_at_once_get_exactly_five_checks_and_then_the_account_is_locked()
    {
        var (t, u) = await SeedUser();

        var results = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(() => Reserve(t, u.Id, Now))));

        results.Count(allowed => allowed).Should().Be(User.MaxFailedAttempts);

        await using var db = pg.AdminContext();
        var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == u.Id);
        stored.IsLocked(Now).Should().BeTrue();
        stored.LockedUntil.Should().BeCloseTo(Now.Add(User.LockDuration), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Attempts_are_counted_one_by_one_and_the_fifth_locks()
    {
        var (t, u) = await SeedUser();

        for (var i = 0; i < User.MaxFailedAttempts; i++) (await Reserve(t, u.Id, Now)).Should().BeTrue();

        (await Reserve(t, u.Id, Now)).Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_lock_lets_attempts_through_again()
    {
        var (t, u) = await SeedUser();
        for (var i = 0; i < User.MaxFailedAttempts; i++) await Reserve(t, u.Id, Now);

        (await Reserve(t, u.Id, Now.Add(User.LockDuration).AddSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task The_user_object_is_refreshed_so_a_lock_is_visible_to_the_caller()
    {
        var (t, u) = await SeedUser();
        await using var db = AppContext(t.Id);
        var repo = new UserRepository(db);
        var user = (await repo.GetCurrentTenantUserAsync(u.Id, default))!;

        for (var i = 0; i < User.MaxFailedAttempts; i++) await repo.TryReserveLoginAttemptAsync(user, Now, default);

        user.IsLocked(Now).Should().BeTrue();
    }
}
