using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class AuditTableTests(PostgresFixture pg)
{
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
        var t = Tenant.Create($"Audit {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static AuditEvent Event(Guid tenantId, long seq, string prevHash)
    {
        var e = AuditEvent.Create(tenantId, DateTimeOffset.UtcNow, "user", Guid.NewGuid(), "policy.publish",
            "policy", Guid.NewGuid(), "success", "203.0.113.9",
            new Dictionary<string, string> { ["version"] = "7", ["approver"] = "A. Rao" }).Value;
        e.Seal(seq, prevHash);
        return e;
    }

    private async Task Save(Guid tenantId, AuditEvent e)
    {
        await using var db = AppContext(tenantId);
        db.AuditEvents.Add(e);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_event_read_back_from_the_database_still_has_a_valid_hash()
    {
        var tenantId = await SeedTenant();
        var saved = Event(tenantId, 1, AuditEvent.Genesis);
        await Save(tenantId, saved);

        await using var db = AppContext(tenantId);
        var loaded = await db.AuditEvents.AsNoTracking().SingleAsync(x => x.Id == saved.Id);

        // Time, ip (inet) and details (jsonb) all took a trip through PostgreSQL, and the hash must still come out the same.
        AuditHasher.Compute(loaded).Should().Be(loaded.Hash).And.Be(saved.Hash);
        loaded.Details.Should().BeEquivalentTo(saved.Details);
        loaded.Ip.Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task The_app_role_cannot_update_or_delete_audit_rows()
    {
        var tenantId = await SeedTenant();
        await Save(tenantId, Event(tenantId, 1, AuditEvent.Genesis));

        await using var db = AppContext(tenantId);
        var update = () => db.Database.ExecuteSqlRawAsync("update audit_events set result = 'denied'");
        await update.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "42501");   // permission denied

        var delete = () => db.Database.ExecuteSqlRawAsync("delete from audit_events");
        await delete.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == "42501");
    }

    [Fact]
    public async Task Even_the_owner_is_stopped_by_the_trigger()
    {
        var tenantId = await SeedTenant();
        await Save(tenantId, Event(tenantId, 1, AuditEvent.Genesis));

        await using var admin = pg.AdminContext();   // superuser: grants and RLS do not apply, the trigger still does
        var update = () => admin.Database.ExecuteSqlRawAsync("update audit_events set result = 'denied'");
        await update.Should().ThrowAsync<PostgresException>().Where(e => e.MessageText.Contains("append-only"));

        var delete = () => admin.Database.ExecuteSqlRawAsync("delete from audit_events");
        await delete.Should().ThrowAsync<PostgresException>().Where(e => e.MessageText.Contains("append-only"));

        var truncate = () => admin.Database.ExecuteSqlRawAsync("truncate table audit_events");
        await truncate.Should().ThrowAsync<PostgresException>().Where(e => e.MessageText.Contains("append-only"));
    }

    [Fact]
    public async Task A_tenant_cannot_see_another_tenants_audit_log()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await Save(a, Event(a, 1, AuditEvent.Genesis));

        await using var db = AppContext(b);
        (await db.AuditEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_cannot_write_into_another_tenants_log()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();

        var act = () => Save(b, Event(a, 1, AuditEvent.Genesis));   // connected as tenant b, row says tenant a

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Two_events_cannot_share_a_sequence_number()
    {
        var tenantId = await SeedTenant();
        await Save(tenantId, Event(tenantId, 1, AuditEvent.Genesis));

        var act = () => Save(tenantId, Event(tenantId, 1, AuditEvent.Genesis));

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}