using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>
/// Penetration test finding VG-PT-002 (PT-AUTH-08): eight requests presenting the same refresh token at the same instant each received
/// their own new token, because each read the session, rotated it in memory and saved. These tests race many swaps against a real
/// PostgreSQL and require that exactly one wins. They use the normal connection pool on purpose (see AtomicOneTimeUseTests).
/// </summary>
[Collection("postgres")]
public class AtomicRefreshRotationTests(PostgresFixture pg)
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

    private async Task<(Tenant Tenant, Session Session, string Token)> SeedSession()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        var t = Tenant.Create($"Rotate {s}").Value;
        var u = User.Invite(t.Id, $"carol-{s}@acme.test", "Carol", UserRole.Developer, Now).Value;
        var token = RefreshTokens.Generate();
        var session = Session.Start(t.Id, u.Id, RefreshTokens.Hash(token), Now).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.Add(u);
        await db.SaveChangesAsync();
        db.Add(session);
        await db.SaveChangesAsync();
        return (t, session, token);
    }

    [Fact]
    public async Task Many_requests_with_the_same_token_get_exactly_one_new_token()
    {
        var (t, session, token) = await SeedSession();
        var presented = RefreshTokens.Hash(token);

        var results = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
        {
            await using var db = AppContext(t.Id);
            var repo = new SessionRepository(db);
            var loaded = (await repo.FindByRefreshHashAsync(presented, default))!;
            var newHash = RefreshTokens.Hash(RefreshTokens.Generate());
            return (Won: await repo.TryRotateAsync(loaded, presented, newHash, Now, default), NewHash: newHash);
        })));

        results.Count(r => r.Won).Should().Be(1);

        await using var check = pg.AdminContext();
        var stored = await check.Sessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        stored.PreviousRefreshHash.Should().Be(presented);
        stored.CurrentRefreshHash.Should().Be(results.Single(r => r.Won).NewHash);   // the winner's token is the one that works
    }

    [Fact]
    public async Task A_revoked_session_cannot_be_rotated()
    {
        var (t, session, token) = await SeedSession();
        await using (var admin = pg.AdminContext())
        {
            var s = await admin.Sessions.SingleAsync(x => x.Id == session.Id);
            s.Revoke("test", Now);
            await admin.SaveChangesAsync();
        }

        await using var db = AppContext(t.Id);
        var repo = new SessionRepository(db);
        var loaded = (await repo.FindByRefreshHashAsync(RefreshTokens.Hash(token), default))!;

        (await repo.TryRotateAsync(loaded, RefreshTokens.Hash(token), RefreshTokens.Hash(RefreshTokens.Generate()), Now, default))
            .Should().BeFalse();
    }
}
