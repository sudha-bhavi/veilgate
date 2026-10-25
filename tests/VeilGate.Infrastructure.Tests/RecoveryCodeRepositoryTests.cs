using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class RecoveryCodeRepositoryTests(PostgresFixture pg)
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

    // Ten real codes, hashed the way production hashes them.
    private static List<(string raw, RecoveryCode entity)> TenCodes(User u) =>
        RecoveryCodes.Generate()
            .Select(raw => (raw, RecoveryCode.Create(u.TenantId, u.Id, RecoveryCodes.Hash(raw), Now).Value))
            .ToList();

    [Fact]
    public async Task Saved_codes_can_be_found_again_by_their_hash()
    {
        var (t, u) = await SeedUser();
        var codes = TenCodes(u);

        await using (var db = AppContext(t.Id))
        {
            var repo = new RecoveryCodeRepository(db);
            repo.AddRange(codes.Select(c => c.entity));
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(t.Id))
        {
            var repo = new RecoveryCodeRepository(db);
            var found = await repo.FindByHashAsync(u.Id, RecoveryCodes.Hash(codes[3].raw), default);

            found.Should().NotBeNull();
            found!.Id.Should().Be(codes[3].entity.Id);
            (await repo.CountUnusedAsync(u.Id, default)).Should().Be(10);
        }
    }

    [Fact]
    public async Task An_unknown_hash_finds_nothing()
    {
        var (t, u) = await SeedUser();

        await using var db = AppContext(t.Id);

        (await new RecoveryCodeRepository(db).FindByHashAsync(u.Id, RecoveryCodes.Hash("AAAAA-BBBBB"), default))
            .Should().BeNull();
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_codes()
    {
        var (t, u) = await SeedUser();
        var codes = TenCodes(u);
        await using (var admin = pg.AdminContext())
        {
            admin.AddRange(codes.Select(c => c.entity));
            await admin.SaveChangesAsync();
        }
        var (otherTenant, _) = await SeedUser();

        await using var db = AppContext(otherTenant.Id);
        var repo = new RecoveryCodeRepository(db);

        (await repo.FindByHashAsync(u.Id, RecoveryCodes.Hash(codes[0].raw), default)).Should().BeNull();
        (await repo.CountUnusedAsync(u.Id, default)).Should().Be(0);
    }

    [Fact]
    public async Task A_spent_code_is_remembered_and_no_longer_counted()
    {
        var (t, u) = await SeedUser();
        var codes = TenCodes(u);
        await using (var admin = pg.AdminContext())
        {
            admin.AddRange(codes.Select(c => c.entity));
            await admin.SaveChangesAsync();
        }

        await using (var db = AppContext(t.Id))
        {
            var repo = new RecoveryCodeRepository(db);
            var code = await repo.FindByHashAsync(u.Id, RecoveryCodes.Hash(codes[0].raw), default);
            code!.MarkUsed(Now).IsSuccess.Should().BeTrue();
            await repo.SaveChangesAsync(default);
        }

        await using (var db = AppContext(t.Id))
        {
            var repo = new RecoveryCodeRepository(db);
            var code = await repo.FindByHashAsync(u.Id, RecoveryCodes.Hash(codes[0].raw), default);

            code!.IsUsed.Should().BeTrue();
            (await repo.CountUnusedAsync(u.Id, default)).Should().Be(9);
        }
    }

    [Fact]
    public async Task The_same_code_cannot_be_stored_twice_for_one_user()
    {
        var (t, u) = await SeedUser();
        var hash = RecoveryCodes.Hash("K7M2P-9QX4H");

        await using var db = AppContext(t.Id);
        var repo = new RecoveryCodeRepository(db);
        repo.AddRange(new[]
        {
            RecoveryCode.Create(t.Id, u.Id, hash, Now).Value,
            RecoveryCode.Create(t.Id, u.Id, hash, Now).Value,
        });

        var act = () => repo.SaveChangesAsync(default);
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}