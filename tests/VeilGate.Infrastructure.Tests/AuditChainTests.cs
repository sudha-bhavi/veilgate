using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class AuditChainTests(PostgresFixture pg)
{
    /// <summary>Contexts built from the single-connection pool must be used one at a time; the concurrency test passes the larger pool.</summary>
    private VeilGateDbContext AppContext(Guid tenantId, string? connection = null)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        var options = new DbContextOptionsBuilder<VeilGateDbContext>()
            .UseNpgsql(connection ?? pg.AppConnection)
            .AddInterceptors(new TenantConnectionInterceptor(tenant))
            .Options;
        return new VeilGateDbContext(options);
    }

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Chain {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static AuditEvent New(Guid tenantId, string action = "auth.login") =>
        AuditEvent.Create(tenantId, DateTimeOffset.UtcNow, "user", Guid.NewGuid(), action, ip: "203.0.113.9").Value;

    private async Task WriteMany(Guid tenantId, int count)
    {
        await using var db = AppContext(tenantId);
        var writer = new HashChainAuditWriter(db);
        for (var i = 0; i < count; i++) await writer.WriteAsync(New(tenantId, $"test.event{i}"));
    }

    private async Task<Application.Abstractions.ChainVerification> Verify(Guid tenantId, long? from = null, long? to = null)
    {
        await using var db = AppContext(tenantId);
        return await new AuditChainVerifier(db).VerifyAsync(tenantId, from, to);
    }

    /// <summary>Changes audit rows the way an attacker with database access would: by switching the trigger off first.</summary>
    private async Task AsAttacker(string sql)
    {
        await using var admin = pg.AdminContext();
        await admin.Database.ExecuteSqlRawAsync("alter table audit_events disable trigger audit_no_update");
        try { await admin.Database.ExecuteSqlRawAsync(sql); }
        finally { await admin.Database.ExecuteSqlRawAsync("alter table audit_events enable trigger audit_no_update"); }
    }

    [Fact]
    public async Task Events_form_a_chain_starting_from_the_genesis_hash()
    {
        var tenantId = await SeedTenant();
        await WriteMany(tenantId, 3);

        await using var db = AppContext(tenantId);
        var rows = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Seq).ToListAsync();

        rows.Select(r => r.Seq).Should().Equal(1, 2, 3);
        rows[0].PrevHash.Should().Be(AuditEvent.Genesis);
        rows[1].PrevHash.Should().Be(rows[0].Hash);
        rows[2].PrevHash.Should().Be(rows[1].Hash);
    }

    [Fact]
    public async Task Every_tenant_has_its_own_chain()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await WriteMany(a, 2);
        await WriteMany(b, 1);

        await using var db = AppContext(b);
        var rows = await db.AuditEvents.AsNoTracking().ToListAsync();

        rows.Should().ContainSingle().Which.Seq.Should().Be(1);
    }

    [Fact]
    public async Task Writers_running_at_the_same_time_cannot_fork_the_chain()
    {
        var tenantId = await SeedTenant();

        var writes = Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = AppContext(tenantId, pg.AppConnectionForApi);   // each writer has its own connection
            await new HashChainAuditWriter(db).WriteAsync(New(tenantId));
        });
        await Task.WhenAll(writes);

        await using (var db = AppContext(tenantId))
            (await db.AuditEvents.OrderBy(x => x.Seq).Select(x => x.Seq).ToListAsync()).Should().Equal(Enumerable.Range(1, 20).Select(i => (long)i));
        (await Verify(tenantId)).Should().Match<Application.Abstractions.ChainVerification>(v => v.Ok && v.Verified == 20);
    }

    [Fact]
    public async Task A_clean_chain_verifies_and_an_empty_one_does_too()
    {
        var tenantId = await SeedTenant();
        (await Verify(tenantId)).Ok.Should().BeTrue();

        await WriteMany(tenantId, 5);
        var result = await Verify(tenantId);

        result.Ok.Should().BeTrue();
        result.Verified.Should().Be(5);
    }

    [Fact]
    public async Task A_range_can_be_verified_on_its_own()
    {
        var tenantId = await SeedTenant();
        await WriteMany(tenantId, 5);

        var result = await Verify(tenantId, from: 2, to: 4);

        result.Ok.Should().BeTrue();
        result.Verified.Should().Be(3);
    }

    [Fact]
    public async Task An_edited_event_is_found()
    {
        var tenantId = await SeedTenant();
        await WriteMany(tenantId, 4);
        await AsAttacker($"update audit_events set action = 'tampered' where tenant_id = '{tenantId}' and seq = 2");

        var result = await Verify(tenantId);

        result.Ok.Should().BeFalse();
        result.BrokenAtSeq.Should().Be(2);
        result.ExpectedHash.Should().NotBe(result.ActualHash);
    }

    [Fact]
    public async Task A_deleted_event_is_found()
    {
        var tenantId = await SeedTenant();
        await WriteMany(tenantId, 4);
        await AsAttacker($"delete from audit_events where tenant_id = '{tenantId}' and seq = 2");

        var result = await Verify(tenantId);

        result.Ok.Should().BeFalse();
        result.BrokenAtSeq.Should().Be(2);   // number 2 is missing
    }

    [Fact]
    public async Task A_broken_link_is_found()
    {
        var tenantId = await SeedTenant();
        await WriteMany(tenantId, 4);
        await AsAttacker($"update audit_events set prev_hash = '{new string('0', 64)}' where tenant_id = '{tenantId}' and seq = 3");

        var result = await Verify(tenantId);

        result.Ok.Should().BeFalse();
        result.BrokenAtSeq.Should().Be(3);
    }
}