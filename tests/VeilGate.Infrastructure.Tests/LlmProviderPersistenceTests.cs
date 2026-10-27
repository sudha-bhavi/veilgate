using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application.Abstractions;
using VeilGate.Application.LlmProviders;
using VeilGate.Domain;
using VeilGate.Domain.LlmProviders;
using VeilGate.Infrastructure.Llm;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

/// <summary>Providers on real PostgreSQL with real envelope encryption: isolation between tenants, tampering, and the constraints.</summary>
[Collection("postgres")]
public class LlmProviderPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(32);
    private const string ApiKey = "sk-live-0123456789WXYZ";

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

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

    private static TenantSecretProtector Protector(VeilGateDbContext db) => new(new LocalKeyProvider(db, new FixedClock(), Kek));

    private static LlmProviderCatalog Catalog(VeilGateDbContext db) =>
        new(new LlmProviderRepository(db), Protector(db), NullLogger<LlmProviderCatalog>.Instance);

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Llm {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>Connects a provider the way the handler does: encrypt the key bound to the new id, then save.</summary>
    private async Task<LlmProvider> Connect(Guid tenantId, string name = "openai", int priority = 1, bool enabled = true,
        string[]? models = null, string apiKey = ApiKey)
    {
        await using var db = AppContext(tenantId);
        var id = Guid.CreateVersion7();
        var secret = await Protector(db).ProtectAsync(tenantId, apiKey, LlmProviderKeys.Context(tenantId, id));
        var provider = LlmProvider.Create(id, tenantId, name, "https://api.example.com", models ?? ["gpt-4o-mini"],
            new ProtectedApiKey(secret.Cipher, secret.DekVersion, LlmProvider.Last4Of(apiKey)), priority: priority, enabled: enabled).Value;
        db.LlmProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    // ------------------------------------------------------------ encryption

    [Fact]
    public async Task The_key_is_stored_encrypted_and_comes_back_only_through_the_catalog()
    {
        var tenantId = await SeedTenant();
        var written = await Connect(tenantId);

        await using (var admin = pg.AdminContext())
        {
            var raw = (await admin.LlmProviders.AsNoTracking().SingleAsync(p => p.Id == written.Id)).ApiKeyCipher;
            Encoding.UTF8.GetString(raw).Should().NotContain("sk-live").And.NotContain("0123456789");
            raw.Length.Should().Be(12 + 16 + ApiKey.Length);   // nonce, tag, ciphertext
        }

        await using var db = AppContext(tenantId);
        var config = await Catalog(db).FindAsync(written.Id);

        config!.ApiKey.Should().Be(ApiKey);
        config.BaseUrl.Should().Be("https://api.example.com");
        config.Models.Should().Equal("gpt-4o-mini");
    }

    [Fact]
    public async Task Encrypting_the_same_key_twice_gives_different_ciphertext()
    {
        var tenantId = await SeedTenant();
        await using var db = AppContext(tenantId);
        var protector = Protector(db);

        var a = await protector.ProtectAsync(tenantId, ApiKey, "ctx");
        var b = await protector.ProtectAsync(tenantId, ApiKey, "ctx");

        a.Cipher.Should().NotEqual(b.Cipher);   // a fresh random nonce every time
        (await protector.UnprotectAsync(tenantId, a.Cipher, a.DekVersion, "ctx")).Should().Be(ApiKey);
        (await protector.UnprotectAsync(tenantId, b.Cipher, b.DekVersion, "ctx")).Should().Be(ApiKey);
    }

    [Fact]
    public async Task The_ciphertext_opens_only_with_the_context_it_was_made_for()
    {
        var tenantId = await SeedTenant();
        await using var db = AppContext(tenantId);
        var protector = Protector(db);
        var secret = await protector.ProtectAsync(tenantId, ApiKey, "provider-1");

        var act = () => protector.UnprotectAsync(tenantId, secret.Cipher, secret.DekVersion, "provider-2");

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task A_flipped_bit_or_a_truncated_value_is_detected()
    {
        var tenantId = await SeedTenant();
        await using var db = AppContext(tenantId);
        var protector = Protector(db);
        var secret = await protector.ProtectAsync(tenantId, ApiKey, "ctx");
        var flipped = secret.Cipher.ToArray();
        flipped[^1] ^= 0x01;

        await FluentActions.Awaiting(() => protector.UnprotectAsync(tenantId, flipped, secret.DekVersion, "ctx")).Should().ThrowAsync<CryptographicException>();
        await FluentActions.Awaiting(() => protector.UnprotectAsync(tenantId, secret.Cipher[..20], secret.DekVersion, "ctx")).Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task A_key_encrypted_for_one_tenant_does_not_open_for_another()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await using var dbA = AppContext(a);
        var secret = await Protector(dbA).ProtectAsync(a, ApiKey, "ctx");

        await using var dbB = AppContext(b);
        var protectorB = Protector(dbB);

        // Tenant B has no data key yet, and tenant A's is invisible to it.
        await FluentActions.Awaiting(() => protectorB.UnprotectAsync(b, secret.Cipher, secret.DekVersion, "ctx")).Should().ThrowAsync<InvalidOperationException>();

        // Once B has a key of its own (version 1, like A's), A's ciphertext still does not open: the keys are different.
        await protectorB.ProtectAsync(b, "anything", "ctx");
        await FluentActions.Awaiting(() => protectorB.UnprotectAsync(b, secret.Cipher, secret.DekVersion, "ctx")).Should().ThrowAsync<CryptographicException>();
    }

    // ------------------------------------------------------------ tampering with rows

    [Fact]
    public async Task A_key_copied_from_one_provider_onto_another_does_not_work_and_the_provider_is_skipped()
    {
        var tenantId = await SeedTenant();
        var first = await Connect(tenantId, "first", priority: 1, apiKey: "first-key-1111");
        var second = await Connect(tenantId, "second", priority: 2, apiKey: "second-key-2222");

        // Someone with write access to the table copies the first provider's encrypted key over the second's.
        await using (var admin = pg.AdminContext())
            await admin.Database.ExecuteSqlInterpolatedAsync(
                $"update llm_providers set api_key_enc = (select api_key_enc from llm_providers where id = {first.Id}) where id = {second.Id}");

        await using var db = AppContext(tenantId);
        var usable = await Catalog(db).GetForModelAsync("gpt-4o-mini");

        usable.Select(p => p.Name).Should().Equal("first");   // the moved key is refused, not used
        (await Catalog(db).FindAsync(second.Id)).Should().BeNull();
        (await Catalog(db).ServesModelAsync("gpt-4o-mini")).Should().BeTrue();   // listing does not decrypt, so it does not notice
    }

    [Fact]
    public async Task A_corrupted_key_makes_only_that_provider_unusable()
    {
        var tenantId = await SeedTenant();
        var good = await Connect(tenantId, "good", priority: 2);
        var bad = await Connect(tenantId, "bad", priority: 1);
        await using (var admin = pg.AdminContext())
            await admin.Database.ExecuteSqlInterpolatedAsync($"update llm_providers set api_key_enc = '\\x0102030405'::bytea where id = {bad.Id}");

        await using var db = AppContext(tenantId);

        (await Catalog(db).GetForModelAsync("gpt-4o-mini")).Select(p => p.Name).Should().Equal("good");
    }

    // ------------------------------------------------------------ the catalog

    [Fact]
    public async Task The_catalog_returns_enabled_providers_that_serve_the_model_in_failover_order()
    {
        var tenantId = await SeedTenant();
        await Connect(tenantId, "late", priority: 9);
        await Connect(tenantId, "early", priority: 1);
        await Connect(tenantId, "off", priority: 2, enabled: false);
        await Connect(tenantId, "other-model", priority: 3, models: ["claude-3"]);

        await using var db = AppContext(tenantId);

        (await Catalog(db).GetForModelAsync("gpt-4o-mini")).Select(p => p.Name).Should().Equal("early", "late");
        (await Catalog(db).GetForModelAsync("claude-3")).Select(p => p.Name).Should().Equal("other-model");
        (await Catalog(db).GetForModelAsync("unknown")).Should().BeEmpty();
        (await Catalog(db).ServesModelAsync("claude-3")).Should().BeTrue();
        (await Catalog(db).ServesModelAsync("unknown")).Should().BeFalse();
    }

    [Fact]
    public async Task A_disabled_provider_alone_serves_nothing_but_can_still_be_found_for_a_test()
    {
        var tenantId = await SeedTenant();
        var off = await Connect(tenantId, "off", enabled: false);

        await using var db = AppContext(tenantId);

        (await Catalog(db).ServesModelAsync("gpt-4o-mini")).Should().BeFalse();
        (await Catalog(db).FindAsync(off.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Another_tenant_cannot_see_or_use_the_provider()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        var provider = await Connect(a);

        await using var db = AppContext(b);
        var catalog = Catalog(db);

        (await catalog.GetForModelAsync("gpt-4o-mini")).Should().BeEmpty();
        (await catalog.ServesModelAsync("gpt-4o-mini")).Should().BeFalse();
        (await catalog.FindAsync(provider.Id)).Should().BeNull();
        (await new LlmProviderRepository(db).ListAsync(default)).Should().BeEmpty();
        (await new LlmProviderRepository(db).FindAsync(provider.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task The_list_is_ordered_by_priority_then_name()
    {
        var tenantId = await SeedTenant();
        await Connect(tenantId, "b", priority: 2);
        await Connect(tenantId, "z", priority: 1);
        await Connect(tenantId, "a", priority: 2);

        await using var db = AppContext(tenantId);

        (await new LlmProviderRepository(db).ListAsync(default)).Select(p => p.Name).Should().Equal("z", "a", "b");
    }

    [Fact]
    public async Task A_name_exists_check_can_leave_one_provider_out()
    {
        var tenantId = await SeedTenant();
        var p = await Connect(tenantId, "openai");

        await using var db = AppContext(tenantId);
        var repo = new LlmProviderRepository(db);

        (await repo.NameExistsAsync("openai", null, default)).Should().BeTrue();
        (await repo.NameExistsAsync("openai", p.Id, default)).Should().BeFalse();
        (await repo.NameExistsAsync("other", null, default)).Should().BeFalse();
    }

    // ------------------------------------------------------------ constraints

    [Fact]
    public async Task A_name_is_unique_within_a_tenant_but_not_across_tenants()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await Connect(a, "shared");

        await Connect(b, "shared");   // another tenant: fine
        var act = () => Connect(a, "shared");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_database_refuses_no_models_a_silly_timeout_and_a_missing_key_version()
    {
        var tenantId = await SeedTenant();
        var p = await Connect(tenantId);
        await using var admin = pg.AdminContext();

        var noModels = () => admin.Database.ExecuteSqlInterpolatedAsync($"update llm_providers set models = ARRAY[]::text[] where id = {p.Id}");
        var timeout = () => admin.Database.ExecuteSqlInterpolatedAsync($"update llm_providers set timeout_seconds = 0 where id = {p.Id}");
        var tooLong = () => admin.Database.ExecuteSqlInterpolatedAsync($"update llm_providers set timeout_seconds = 301 where id = {p.Id}");
        var dek = () => admin.Database.ExecuteSqlInterpolatedAsync($"update llm_providers set dek_version = 0 where id = {p.Id}");

        await noModels.Should().ThrowAsync<Exception>();
        await timeout.Should().ThrowAsync<Exception>();
        await tooLong.Should().ThrowAsync<Exception>();
        await dek.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_app_role_cannot_write_a_provider_into_another_tenant()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        var sneaky = LlmProvider.Create(Guid.CreateVersion7(), b, "sneaky", "https://api.example.com", ["m"], new ProtectedApiKey([1], 1, "abcd")).Value;

        await using var db = AppContext(a);   // signed in as tenant A, writing a row that says tenant B
        db.LlmProviders.Add(sneaky);
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_provider_survives_a_round_trip_with_its_lists_and_settings()
    {
        var tenantId = await SeedTenant();
        var written = await Connect(tenantId, "openai", priority: 7, models: ["gpt-4o", "gpt-4o-mini"]);

        await using var db = AppContext(tenantId);
        var read = await new LlmProviderRepository(db).FindAsync(written.Id, default);

        read!.Models.Should().Equal("gpt-4o", "gpt-4o-mini");
        read.Priority.Should().Be(7);
        read.TimeoutSeconds.Should().Be(60);
        read.Enabled.Should().BeTrue();
        read.KeyLast4.Should().Be("WXYZ");
        read.DekVersion.Should().Be(1);
        read.TenantId.Should().Be(tenantId);
    }
}
