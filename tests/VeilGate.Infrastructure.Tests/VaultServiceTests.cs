using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Detection;
using VeilGate.Application.Policies;
using VeilGate.Application.Transformation;
using VeilGate.Domain;
using VeilGate.Domain.Vault;
using VeilGate.Infrastructure.Detection;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class VaultServiceTests(PostgresFixture pg)
{
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(32);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>A context like the API builds. The app connection has a pool of one, so never keep two of these open at once.</summary>
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

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Vault {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>Runs one action the way one request would: its own context, its own service instances.</summary>
    private async Task<T> Request<T>(Guid tenantId, IClock clock, Func<AesGcmVaultService, Task<T>> action)
    {
        await using var db = AppContext(tenantId);
        var vault = new AesGcmVaultService(db, new LocalKeyProvider(db, clock, Kek), clock);
        return await action(vault);
    }

    private Task Request(Guid tenantId, IClock clock, Func<AesGcmVaultService, Task> action) =>
        Request<int>(tenantId, clock, async v => { await action(v); return 0; });

    private async Task<VaultSession> StoreOne(Guid tenantId, Guid clientId, IClock clock, string value = "John Smith")
    {
        return await Request(tenantId, clock, async vault =>
        {
            var session = await vault.OpenAsync(tenantId, clientId, TimeSpan.FromHours(1));
            session.TokenFor("PERSON", value);
            await vault.StoreAsync(session);
            return session;
        });
    }

    [Fact]
    public async Task A_stored_value_can_be_restored_by_a_later_request()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stored = await StoreOne(tenantId, clientId, clock);
        var token = stored.Tokens.Keys.Single();

        var restored = await Request(tenantId, clock, async vault =>
        {
            var session = await vault.FindAsync(stored.Id, clientId);
            return session is null ? null : await vault.ResolveAsync(session, token);
        });

        restored.Should().Be("John Smith");
    }

    [Fact]
    public async Task The_database_never_holds_the_original_value()
    {
        var tenantId = await SeedTenant();
        var stored = await StoreOne(tenantId, Guid.NewGuid(), new FixedClock(DateTimeOffset.UtcNow), "Zebediah Quux");

        await using var admin = pg.AdminContext();
        var row = await admin.VaultTokens.SingleAsync(t => t.SessionId == stored.Id);

        row.Ciphertext.Should().NotEqual(Encoding.UTF8.GetBytes("Zebediah Quux"));
        row.Nonce.Should().HaveCount(12);
        row.Tag.Should().HaveCount(16);
        row.EntityType.Should().Be("PERSON");
    }

    [Fact]
    public async Task Another_tenant_cannot_find_the_session()
    {
        var owner = await SeedTenant();
        var other = await SeedTenant();
        var clientId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stored = await StoreOne(owner, clientId, clock);

        var found = await Request(other, clock, v => v.FindAsync(stored.Id, clientId));

        found.Should().BeNull();
    }

    [Fact]
    public async Task Another_client_of_the_same_tenant_cannot_find_the_session()
    {
        var tenantId = await SeedTenant();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stored = await StoreOne(tenantId, Guid.NewGuid(), clock);

        var found = await Request(tenantId, clock, v => v.FindAsync(stored.Id, Guid.NewGuid()));

        found.Should().BeNull();
    }

    [Fact]
    public async Task An_expired_session_cannot_be_found()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stored = await StoreOne(tenantId, clientId, clock);

        clock.UtcNow = clock.UtcNow.AddHours(2);
        var found = await Request(tenantId, clock, v => v.FindAsync(stored.Id, clientId));

        found.Should().BeNull();
    }

    [Fact]
    public async Task Storing_twice_does_not_duplicate_tokens()
    {
        var tenantId = await SeedTenant();
        var clock = new FixedClock(DateTimeOffset.UtcNow);

        var session = await Request(tenantId, clock, async vault =>
        {
            var s = await vault.OpenAsync(tenantId, Guid.NewGuid(), TimeSpan.FromHours(1));
            s.TokenFor("PERSON", "John Smith");
            await vault.StoreAsync(s);
            s.TokenFor("EMAIL", "a@b.test");   // a new value appears in a later step
            await vault.StoreAsync(s);
            return s;
        });

        await using var admin = pg.AdminContext();
        (await admin.VaultTokens.CountAsync(t => t.SessionId == session.Id)).Should().Be(2);
    }

    [Fact]
    public async Task A_tenant_gets_one_data_key_which_is_stored_wrapped()
    {
        var tenantId = await SeedTenant();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        await StoreOne(tenantId, Guid.NewGuid(), clock);
        await StoreOne(tenantId, Guid.NewGuid(), clock);

        await using var admin = pg.AdminContext();
        var keys = await admin.TenantKeys.Where(k => k.TenantId == tenantId).ToListAsync();
        keys.Should().ContainSingle().Which.WrappedDek.Length.Should().BeGreaterThan(32);   // nonce + tag + key, base64, not the bare key
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_data_key_rows_or_tokens()
    {
        var owner = await SeedTenant();
        var other = await SeedTenant();
        await StoreOne(owner, Guid.NewGuid(), new FixedClock(DateTimeOffset.UtcNow));

        await using var db = AppContext(other);
        (await db.TenantKeys.CountAsync()).Should().Be(0);
        (await db.VaultTokens.CountAsync()).Should().Be(0);
        (await db.VaultSessions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_tampered_ciphertext_is_detected_not_returned()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stored = await StoreOne(tenantId, clientId, clock);
        var token = stored.Tokens.Keys.Single();

        await using (var admin = pg.AdminContext())
            await admin.Database.ExecuteSqlRawAsync("update vault_tokens set ciphertext = set_byte(ciphertext, 0, get_byte(ciphertext, 0) # 255)");

        var act = () => Request(tenantId, clock, v => v.ResolveAsync(stored, token));

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task Purging_removes_expired_sessions_and_their_tokens()
    {
        var tenantId = await SeedTenant();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var stored = await StoreOne(tenantId, Guid.NewGuid(), clock);

        clock.UtcNow = clock.UtcNow.AddHours(2);
        var removed = await Request(tenantId, clock, v => v.PurgeExpiredAsync());

        removed.Should().Be(1);
        await using var admin = pg.AdminContext();
        (await admin.VaultTokens.CountAsync(t => t.SessionId == stored.Id)).Should().Be(0);
    }

    [Fact]
    public async Task The_date_shift_key_is_stable_for_a_tenant_and_different_between_tenants()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        var clock = new FixedClock(DateTimeOffset.UtcNow);

        var a1 = await Request(a, clock, v => v.OpenAsync(a, Guid.NewGuid(), TimeSpan.FromHours(1)));
        var a2 = await Request(a, clock, v => v.OpenAsync(a, Guid.NewGuid(), TimeSpan.FromHours(1)));
        var b1 = await Request(b, clock, v => v.OpenAsync(b, Guid.NewGuid(), TimeSpan.FromHours(1)));

        a1.DateShiftKey.Should().Equal(a2.DateShiftKey).And.HaveCount(32);
        b1.DateShiftKey.Should().NotEqual(a1.DateShiftKey);
    }

    [Fact]
    public async Task The_whole_gateway_flow_protect_store_and_restore()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var policy = PolicyParser.Parse("""
            version: 1
            name: Flow
            defaults: { action: REDACT }
            entities:
              EMAIL: { action: TOKENIZE }
              PHONE: { action: MASK }
              DATE:  { action: SHIFT, maxDays: 30 }
            """).Policy!;
        var text = "Mail a@b.test or call 513-555-0142 about the 03/14/2025 visit";

        // Request 1: detect, decide, transform, check, store.
        var (sanitized, sessionId) = await Request(tenantId, clock, async vault =>
        {
            var pipeline = new DetectionPipeline([new RegexDetector()]);
            var found = await pipeline.DetectAsync(text, DetectionContext.Default);
            var decision = new PolicyEngine().Decide(found, policy);
            var session = await vault.OpenAsync(tenantId, clientId, TimeSpan.FromHours(1));
            var transformed = new Transformer().Apply(text, decision, session);
            var gate = await new OutboundLeakGate(pipeline).CheckAsync(transformed, policy, DetectionContext.Default);
            gate.Tripped.Should().BeFalse();
            await vault.StoreAsync(session);
            return (transformed.Text, session.Id);
        });

        sanitized.Should().NotContain("a@b.test").And.NotContain("513-555-0142").And.NotContain("03/14/2025");
        sanitized.Should().Contain("[PHONE]");

        // Request 2 (the model answered using the token): find the session and restore.
        var answer = $"I will reply to {System.Text.RegularExpressions.Regex.Match(sanitized, @"\[\[EMAIL_[A-Z2-7]{4}\]\]").Value} today.";
        var restored = await Request(tenantId, clock, async vault =>
        {
            var session = await vault.FindAsync(sessionId, clientId);
            return await new Rehydrator(vault).RestoreAsync(answer, session!);
        });

        restored.Text.Should().Be("I will reply to a@b.test today.");
        restored.Replaced.Should().Be(1);
    }
}
