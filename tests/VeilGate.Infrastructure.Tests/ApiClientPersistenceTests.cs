using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.ApiClients;
using VeilGate.Domain.Policies;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>API clients on real PostgreSQL: row-level security, the lookup that runs before the tenant is known, and the constraints.</summary>
[Collection("postgres")]
public class ApiClientPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private VeilGateDbContext AppContext(Guid? tenantId)
    {
        var tenant = new TenantContext();
        if (tenantId is { } id) tenant.Set(id);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    private async Task<(Guid tenantId, Guid userId)> SeedTenant()
    {
        var t = Tenant.Create($"Cli {Guid.NewGuid():N}"[..20]).Value;
        var u = User.Invite(t.Id, $"dev-{Guid.NewGuid():N}@acme.test", "Dev", UserRole.Developer, Now).Value;
        await using var db = pg.AdminContext();
        db.AddRange(t, u);
        await db.SaveChangesAsync();
        return (t.Id, u.Id);
    }

    private async Task<ApiClient> SeedClient(Guid tenantId, Guid userId, string name = "ehr-bridge", string[]? ips = null, bool secret = true)
    {
        var client = ApiClient.Create(tenantId, userId, name, ["gateway:invoke", "gateway:deidentify"], ipAllowlist: ips, rateLimitPerMinute: 120).Value;
        await using var db = AppContext(tenantId);
        db.ApiClients.Add(client);
        if (secret) db.ApiClientSecrets.Add(ApiClientSecret.Create(tenantId, client.Id, "$argon2id$stub").Value);
        await db.SaveChangesAsync();
        return client;
    }

    // ------------------------------------------------------------ round trip and isolation

    [Fact]
    public async Task A_client_survives_a_round_trip_with_its_lists()
    {
        var (tenantId, userId) = await SeedTenant();
        var written = await SeedClient(tenantId, userId, ips: ["203.0.113.0/24", "2001:db8::/32"]);

        await using var db = AppContext(tenantId);
        var read = await new ApiClientRepository(db).FindAsync(written.Id, default);

        read.Should().NotBeNull();
        read!.ClientId.Should().Be(written.ClientId);
        read.Scopes.Should().Equal("gateway:invoke", "gateway:deidentify");
        read.IpAllowlist.Should().Equal("203.0.113.0/24", "2001:db8::/32");
        read.RateLimitPerMinute.Should().Be(120);
        read.Status.Should().Be(ApiClientStatus.Active);
        read.OwnerUserId.Should().Be(userId);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_client_or_its_secrets()
    {
        var (a, userA) = await SeedTenant();
        var (b, _) = await SeedTenant();
        var client = await SeedClient(a, userA);

        await using var other = AppContext(b);
        IApiClientRepository repo = new ApiClientRepository(other);

        (await repo.FindAsync(client.Id, default)).Should().BeNull();
        (await repo.FindByClientIdAsync(client.ClientId, default)).Should().BeNull();
        (await repo.GetStateAsync(client.Id, default)).Should().BeNull();
        (await repo.ListSecretsAsync(client.Id, default)).Should().BeEmpty();
        (await repo.ListAsync(null, null, null, 1, 25, default)).Total.Should().Be(0);
    }

    [Fact]
    public async Task The_light_state_read_returns_what_the_gateway_checks_on_every_call()
    {
        var (tenantId, userId) = await SeedTenant();
        var client = await SeedClient(tenantId, userId, ips: ["203.0.113.0/24"]);

        await using var db = AppContext(tenantId);
        var state = await new ApiClientRepository(db).GetStateAsync(client.Id, default);

        state!.Status.Should().Be(ApiClientStatus.Active);
        state.IpAllowlist.Should().Equal("203.0.113.0/24");
        state.DefaultPolicyId.Should().BeNull();
    }

    [Fact]
    public async Task Secrets_are_listed_with_their_end_dates()
    {
        var (tenantId, userId) = await SeedTenant();
        var client = await SeedClient(tenantId, userId, secret: false);
        await using (var db = AppContext(tenantId))
        {
            var old = ApiClientSecret.Create(tenantId, client.Id, "$argon2id$old").Value;
            old.ExpireNoLaterThan(Now.AddHours(24));
            db.ApiClientSecrets.AddRange(old, ApiClientSecret.Create(tenantId, client.Id, "$argon2id$new").Value);
            await db.SaveChangesAsync();
        }

        await using var read = AppContext(tenantId);
        var secrets = await new ApiClientRepository(read).ListSecretsAsync(client.Id, default);

        secrets.Should().HaveCount(2);
        secrets.Count(s => s.ExpiresAt == Now.AddHours(24)).Should().Be(1);
        secrets.Count(s => s.ExpiresAt is null).Should().Be(1);
    }

    [Fact]
    public async Task Listing_filters_by_owner_status_and_name()
    {
        var (tenantId, userId) = await SeedTenant();
        await using (var admin = pg.AdminContext())
        {
            var second = User.Invite(tenantId, $"dev2-{Guid.NewGuid():N}@acme.test", "Dev2", UserRole.Developer, Now).Value;
            admin.Add(second);
            await admin.SaveChangesAsync();
            var mine = await SeedClient(tenantId, userId, "Alpha bridge");
            var theirs = ApiClient.Create(tenantId, second.Id, "Beta job", ["gateway:detect"]).Value;
            var revoked = await SeedClient(tenantId, userId, "Gamma old");
            await using var db = AppContext(tenantId);
            db.ApiClients.Add(theirs);
            var toRevoke = await db.ApiClients.SingleAsync(c => c.Id == revoked.Id);
            toRevoke.Revoke(Now);
            await db.SaveChangesAsync();
        }

        await using var read = AppContext(tenantId);
        var repo = new ApiClientRepository(read);

        (await repo.ListAsync(null, null, null, 1, 25, default)).Items.Select(c => c.Name).Should().Equal("Alpha bridge", "Beta job", "Gamma old");
        (await repo.ListAsync(userId, null, null, 1, 25, default)).Items.Select(c => c.Name).Should().Equal("Alpha bridge", "Gamma old");
        (await repo.ListAsync(null, "Revoked", null, 1, 25, default)).Items.Select(c => c.Name).Should().Equal("Gamma old");
        (await repo.ListAsync(null, null, "BETA", 1, 25, default)).Items.Select(c => c.Name).Should().Equal("Beta job");
        (await repo.ListAsync(null, null, "%", 1, 25, default)).Total.Should().Be(0);
        (await repo.ListAsync(null, null, null, 2, 2, default)).Items.Select(c => c.Name).Should().Equal("Gamma old");
    }

    // ------------------------------------------------------------ the lookup before the tenant is known

    [Fact]
    public async Task The_token_lookup_finds_the_tenant_without_any_tenant_being_set()
    {
        var (tenantId, userId) = await SeedTenant();
        var client = await SeedClient(tenantId, userId);

        await using var db = AppContext(null);   // no tenant: row-level security would show nothing
        IApiClientRepository repo = new ApiClientRepository(db);

        (await repo.FindByClientIdAsync(client.ClientId, default)).Should().BeNull("a direct read still sees no rows");
        (await repo.FindTenantByClientIdAsync(client.ClientId, default)).Should().Be(tenantId);
    }

    [Fact]
    public async Task The_token_lookup_does_not_find_unknown_or_revoked_clients()
    {
        var (tenantId, userId) = await SeedTenant();
        var client = await SeedClient(tenantId, userId);
        await using (var db = AppContext(tenantId))
        {
            var row = await db.ApiClients.SingleAsync(c => c.Id == client.Id);
            row.Revoke(Now);
            await db.SaveChangesAsync();
        }

        await using var anon = AppContext(null);
        IApiClientRepository repo = new ApiClientRepository(anon);

        (await repo.FindTenantByClientIdAsync(client.ClientId, default)).Should().BeNull();
        (await repo.FindTenantByClientIdAsync("vg_doesnotexist", default)).Should().BeNull();
    }

    [Fact]
    public async Task The_token_lookup_returns_only_a_tenant_id_and_cannot_be_used_to_read_rows()
    {
        var (tenantId, userId) = await SeedTenant();
        await SeedClient(tenantId, userId);

        await using var anon = AppContext(null);

        (await anon.ApiClients.CountAsync()).Should().Be(0);
        (await anon.ApiClientSecrets.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------ constraints

    [Fact]
    public async Task A_public_client_id_is_unique_across_all_tenants()
    {
        var (a, userA) = await SeedTenant();
        var (b, userB) = await SeedTenant();
        var first = await SeedClient(a, userA);
        var second = await SeedClient(b, userB);

        await using var db = pg.AdminContext();
        var act = () => db.Database.ExecuteSqlInterpolatedAsync($"update api_clients set client_id = {first.ClientId} where id = {second.Id}");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_name_is_unique_within_a_tenant_but_not_across_tenants()
    {
        var (a, userA) = await SeedTenant();
        var (b, userB) = await SeedTenant();
        await SeedClient(a, userA, "shared-name");

        await SeedClient(b, userB, "shared-name");   // another tenant: fine
        var act = () => SeedClient(a, userA, "shared-name");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_database_refuses_an_unknown_scope_an_empty_scope_list_and_a_silly_rate_limit()
    {
        var (tenantId, userId) = await SeedTenant();
        var client = await SeedClient(tenantId, userId);
        await using var db = pg.AdminContext();

        var unknown = () => db.Database.ExecuteSqlInterpolatedAsync($"update api_clients set scopes = ARRAY['gateway:root']::text[] where id = {client.Id}");
        var empty = () => db.Database.ExecuteSqlInterpolatedAsync($"update api_clients set scopes = ARRAY[]::text[] where id = {client.Id}");
        var rate = () => db.Database.ExecuteSqlInterpolatedAsync($"update api_clients set rate_limit_per_minute = 0 where id = {client.Id}");
        var status = () => db.Database.ExecuteSqlInterpolatedAsync($"update api_clients set status = 'Paused' where id = {client.Id}");

        await unknown.Should().ThrowAsync<Exception>();
        await empty.Should().ThrowAsync<Exception>();
        await rate.Should().ThrowAsync<Exception>();
        await status.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_client_cannot_default_to_another_tenants_policy()
    {
        var (a, userA) = await SeedTenant();
        var (b, userB) = await SeedTenant();
        var client = await SeedClient(a, userA);
        var foreign = Policy.Create(b, "Theirs", null).Value;
        await using (var db = AppContext(b))
        {
            db.Policies.Add(foreign);
            await db.SaveChangesAsync();
        }

        await using var admin = pg.AdminContext();
        var act = () => admin.Database.ExecuteSqlInterpolatedAsync($"update api_clients set default_policy_id = {foreign.Id} where id = {client.Id}");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_secret_cannot_be_attached_to_another_tenants_client()
    {
        var (a, userA) = await SeedTenant();
        var (b, _) = await SeedTenant();
        var client = await SeedClient(a, userA);

        await using var db = AppContext(b);
        db.ApiClientSecrets.Add(ApiClientSecret.Create(b, client.Id, "$argon2id$sneaky").Value);
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_app_role_cannot_write_a_client_into_another_tenant()
    {
        var (a, _) = await SeedTenant();
        var (b, userB) = await SeedTenant();
        var sneaky = ApiClient.Create(b, userB, "sneaky", ["gateway:invoke"]).Value;

        await using var db = AppContext(a);   // signed in as tenant A, writing a row that says tenant B
        db.ApiClients.Add(sneaky);
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<Exception>();
    }
}
