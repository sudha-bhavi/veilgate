using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>
/// Penetration test finding VG-PT-001 (PT-AUTH-06 and PT-AUTH-03): a one-time recovery code, or a one-time authenticator code, could be
/// used by two requests at the same instant, because each request read it as "unused", marked it in memory, and saved later.
/// These tests start many requests at once against a real PostgreSQL and require that exactly one of them wins.
///
/// They use the normal connection pool on purpose. The shared test connection allows only ONE connection at a time, which would make
/// the requests queue up politely and hide the race.
/// </summary>
[Collection("postgres")]
public class AtomicOneTimeUseTests(PostgresFixture pg)
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
        var t = Tenant.Create($"OneTime {s}").Value;
        var u = User.Invite(t.Id, $"alice-{s}@acme.test", "Alice", UserRole.TenantAdmin, Now).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        db.Add(u);
        await db.SaveChangesAsync();
        return (t, u);
    }

    // ------------------------------------------------------------------ recovery codes

    private async Task<(Tenant Tenant, User User, List<(string Raw, RecoveryCode Entity)> Codes)> SeedWithCodes()
    {
        var (t, u) = await SeedUser();
        var codes = RecoveryCodes.Generate().Select(raw => (raw, RecoveryCode.Create(t.Id, u.Id, RecoveryCodes.Hash(raw), Now).Value)).ToList();
        await using var db = pg.AdminContext();
        db.RecoveryCodes.AddRange(codes.Select(c => c.Item2));
        await db.SaveChangesAsync();
        return (t, u, codes);
    }

    [Fact]
    public async Task A_recovery_code_is_spent_by_exactly_one_of_many_requests_that_arrive_together()
    {
        var (t, u, codes) = await SeedWithCodes();
        var hash = RecoveryCodes.Hash(codes[0].Raw);

        // Every request first loads the code (all of them see it as unused), and only then do they all try to spend it at once.
        var contexts = Enumerable.Range(0, Racers).Select(_ => AppContext(t.Id)).ToList();
        try
        {
            var loaded = await Task.WhenAll(contexts.Select(c => new RecoveryCodeRepository(c).FindByHashAsync(u.Id, hash, default)));
            loaded.Should().OnlyContain(code => code != null && !code.IsUsed);

            var results = await Task.WhenAll(contexts.Select((c, i) => new RecoveryCodeRepository(c).TrySpendAsync(loaded[i]!, Now, default)));

            results.Count(spent => spent).Should().Be(1, "a one-time code must work for exactly one request");
        }
        finally { foreach (var c in contexts) await c.DisposeAsync(); }

        await using var check = pg.AdminContext();
        (await check.RecoveryCodes.CountAsync(c => c.UserId == u.Id && c.UsedAt != null)).Should().Be(1);
    }

    [Fact]
    public async Task A_spent_recovery_code_stays_spent()
    {
        var (t, u, codes) = await SeedWithCodes();
        var hash = RecoveryCodes.Hash(codes[0].Raw);

        await using var first = AppContext(t.Id);
        var firstRepo = new RecoveryCodeRepository(first);
        (await firstRepo.TrySpendAsync((await firstRepo.FindByHashAsync(u.Id, hash, default))!, Now, default)).Should().BeTrue();

        await using var second = AppContext(t.Id);
        var secondRepo = new RecoveryCodeRepository(second);
        (await secondRepo.TrySpendAsync((await secondRepo.FindByHashAsync(u.Id, hash, default))!, Now, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Spending_one_recovery_code_leaves_the_others_unspent()
    {
        var (t, u, codes) = await SeedWithCodes();

        await using var db = AppContext(t.Id);
        var repo = new RecoveryCodeRepository(db);
        await repo.TrySpendAsync((await repo.FindByHashAsync(u.Id, RecoveryCodes.Hash(codes[3].Raw), default))!, Now, default);

        (await repo.CountUnusedAsync(u.Id, default)).Should().Be(codes.Count - 1);
    }

    [Fact]
    public async Task A_recovery_code_of_another_tenant_cannot_be_spent()
    {
        var (_, _, codes) = await SeedWithCodes();
        var (otherTenant, _) = await SeedUser();

        await using var db = AppContext(otherTenant.Id);   // row-level security hides the code, so nothing is updated
        (await new RecoveryCodeRepository(db).TrySpendAsync(codes[0].Entity, Now, default)).Should().BeFalse();
    }

    // ----------------------------------------------------------- authenticator code windows

    private async Task<(Tenant Tenant, User User, MfaFactor Factor)> SeedConfirmedFactor(long lastUsed = 5)
    {
        var (t, u) = await SeedUser();
        var factor = MfaFactor.EnrollTotp(t.Id, u.Id, "v1:secret", Now).Value;
        factor.Confirm(lastUsed, Now);
        await using var db = pg.AdminContext();
        db.MfaFactors.Add(factor);
        await db.SaveChangesAsync();
        return (t, u, factor);
    }

    [Fact]
    public async Task An_authenticator_code_window_is_accepted_by_exactly_one_of_many_requests_that_arrive_together()
    {
        var (t, u, _) = await SeedConfirmedFactor();

        var contexts = Enumerable.Range(0, Racers).Select(_ => AppContext(t.Id)).ToList();
        try
        {
            var loaded = await Task.WhenAll(contexts.Select(c => new MfaFactorRepository(c).GetTotpForUserAsync(u.Id, default)));
            loaded.Should().OnlyContain(f => f != null && f.LastUsedCounter == 5);   // all of them still see the old window

            var results = await Task.WhenAll(contexts.Select((c, i) => new MfaFactorRepository(c).TryRecordUseAsync(loaded[i]!, 100, Now, default)));

            results.Count(accepted => accepted).Should().Be(1, "a one-time code must work for exactly one request");
        }
        finally { foreach (var c in contexts) await c.DisposeAsync(); }

        await using var check = pg.AdminContext();
        (await check.MfaFactors.SingleAsync(f => f.UserId == u.Id)).LastUsedCounter.Should().Be(100);
    }

    [Fact]
    public async Task The_same_or_an_older_window_is_refused_and_a_newer_one_is_accepted()
    {
        var (t, u, _) = await SeedConfirmedFactor();
        await using var db = AppContext(t.Id);
        var repo = new MfaFactorRepository(db);
        var factor = (await repo.GetTotpForUserAsync(u.Id, default))!;

        (await repo.TryRecordUseAsync(factor, 100, Now, default)).Should().BeTrue();
        (await repo.TryRecordUseAsync(factor, 100, Now, default)).Should().BeFalse();   // the same window again
        (await repo.TryRecordUseAsync(factor, 99, Now, default)).Should().BeFalse();    // an older one
        (await repo.TryRecordUseAsync(factor, 101, Now, default)).Should().BeTrue();    // the next one is fine
    }

    [Fact]
    public async Task A_factor_that_was_never_confirmed_accepts_no_code()
    {
        var (t, u) = await SeedUser();
        var factor = MfaFactor.EnrollTotp(t.Id, u.Id, "v1:secret", Now).Value;
        await using (var admin = pg.AdminContext()) { admin.MfaFactors.Add(factor); await admin.SaveChangesAsync(); }

        await using var db = AppContext(t.Id);
        var repo = new MfaFactorRepository(db);

        (await repo.TryRecordUseAsync((await repo.GetTotpForUserAsync(u.Id, default))!, 100, Now, default)).Should().BeFalse();
    }
}
