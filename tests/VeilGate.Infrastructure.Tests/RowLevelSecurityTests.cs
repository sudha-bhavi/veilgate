using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeilGate.Domain;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class RowLevelSecurityTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>A context exactly like the API builds: connects as vg_app and sets app.tenant_id when a tenant is known.</summary>
    private VeilGateDbContext AppContext(Guid? tenantId)
    {
        var tenant = new TenantContext();
        if (tenantId.HasValue) tenant.Set(tenantId.Value);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    /// <summary>Seeds two tenants with one user each, as the superuser (which bypasses RLS).</summary>
    private async Task<(Tenant a, Tenant b, User ua, User ub)> SeedTwoTenants()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var a = Tenant.Create($"Acme {suffix}").Value;
        var b = Tenant.Create($"Beta {suffix}").Value;
        var ua = User.Invite(a.Id, $"alice-{suffix}@acme.test", "Alice", UserRole.TenantAdmin, Now).Value;
        var ub = User.Invite(b.Id, $"bob-{suffix}@beta.test", "Bob", UserRole.Developer, Now).Value;
        await using var db = pg.AdminContext();
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        db.AddRange(ua, ub);
        await db.SaveChangesAsync();
        return (a, b, ua, ub);
    }

    [Fact]
    public async Task Tenant_sees_only_its_own_users()
    {
        var (a, b, ua, ub) = await SeedTwoTenants();

        await using (var db = AppContext(a.Id))
        {
            var ids = await db.Users.Select(u => u.Id).ToListAsync();
            ids.Should().Contain(ua.Id).And.NotContain(ub.Id);
        }
        await using (var db = AppContext(b.Id))
        {
            var ids = await db.Users.Select(u => u.Id).ToListAsync();
            ids.Should().Contain(ub.Id).And.NotContain(ua.Id);
        }
    }

    [Fact]
    public async Task Fetching_another_tenants_user_by_id_finds_nothing()
    {
        var (a, _, _, ub) = await SeedTwoTenants();
        await using var db = AppContext(a.Id);
        (await db.Users.FirstOrDefaultAsync(u => u.Id == ub.Id)).Should().BeNull();
    }

    [Fact]
    public async Task No_tenant_set_means_zero_rows()
    {
        await SeedTwoTenants();
        await using var db = AppContext(null);
        (await db.Users.CountAsync()).Should().Be(0);
        (await db.Tenants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Tenant_sees_only_its_own_tenant_row()
    {
        var (a, b, _, _) = await SeedTwoTenants();
        await using var db = AppContext(a.Id);
        var ids = await db.Tenants.Select(t => t.Id).ToListAsync();
        ids.Should().ContainSingle().Which.Should().Be(a.Id);
        ids.Should().NotContain(b.Id);
    }

    [Fact]
    public async Task Cannot_insert_a_user_into_another_tenant()
    {
        var (a, b, _, _) = await SeedTwoTenants();
        var sneaky = User.Invite(b.Id, $"sneaky-{Guid.NewGuid():N}@beta.test", "Sneaky", UserRole.Developer, Now).Value;

        await using var db = AppContext(a.Id); // signed in to tenant A, tries to write into tenant B
        db.Users.Add(sneaky);
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task App_role_cannot_create_or_change_tenants()
    {
        var (a, _, _, _) = await SeedTwoTenants();
        await using var db = AppContext(a.Id);
        db.Tenants.Add(Tenant.Create($"Rogue {Guid.NewGuid():N}").Value);
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Tenant_setting_does_not_leak_through_the_connection_pool()
    {
        var (a, _, _, _) = await SeedTwoTenants();

        await using (var first = AppContext(a.Id))
            (await first.Users.CountAsync()).Should().BeGreaterThan(0);

        // Pool size is 1, so this request reuses the same physical connection that just served tenant A.
        await using var second = AppContext(null);
        (await second.Users.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Login_lookup_finds_a_user_by_email_without_a_tenant()
    {
        var (_, _, ua, _) = await SeedTwoTenants();
        await using var c = new NpgsqlConnection(pg.AppConnection);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("select id, tenant_id, role, status from auth_find_user_by_email(@e)", c);
        cmd.Parameters.AddWithValue("e", NpgsqlTypes.NpgsqlDbType.Citext, ua.Email);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        r.GetGuid(0).Should().Be(ua.Id);
        r.GetGuid(1).Should().Be(ua.TenantId);
        r.GetString(2).Should().Be("TenantAdmin");
    }

    [Fact]
    public async Task Emails_are_unique_across_all_tenants_ignoring_case()
    {
        var (a, b, ua, _) = await SeedTwoTenants();
        var dup = User.Invite(b.Id, ua.Email.ToUpperInvariant(), "Dup", UserRole.Auditor, Now).Value;
        await using var db = pg.AdminContext();
        db.Users.Add(dup);
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
