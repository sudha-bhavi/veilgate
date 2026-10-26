using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class SecurityEventTests(PostgresFixture pg)
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
        var t = Tenant.Create($"SecEv {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static SecurityEvent Leak(Guid tenantId) => SecurityEvent.Create(tenantId, DateTimeOffset.UtcNow, Severity.High, "leak.gate_tripped",
        clientId: Guid.NewGuid(), ip: "203.0.113.9", details: new Dictionary<string, string> { ["types"] = "PHONE,SSN" }).Value;

    [Fact]
    public async Task A_security_event_survives_a_round_trip_to_the_database()
    {
        var tenantId = await SeedTenant();
        var e = Leak(tenantId);
        await using (var db = AppContext(tenantId))
            await ((ISecurityEventWriter)new SecurityEventWriter(db)).WriteAsync(e);

        await using var read = AppContext(tenantId);
        var loaded = await read.SecurityEvents.AsNoTracking().SingleAsync(x => x.Id == e.Id);

        loaded.Severity.Should().Be(Severity.High);
        loaded.Type.Should().Be("leak.gate_tripped");
        loaded.Ip.Should().Be("203.0.113.9");
        loaded.Details.Should().BeEquivalentTo(e.Details);
        loaded.OccurredAt.Should().Be(e.OccurredAt);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_event()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        await using (var db = AppContext(a))
            await new SecurityEventWriter(db).WriteAsync(Leak(a));

        await using var other = AppContext(b);
        (await other.SecurityEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_event_can_be_acknowledged_once()
    {
        var tenantId = await SeedTenant();
        var e = Leak(tenantId);
        await using (var db = AppContext(tenantId))
            await new SecurityEventWriter(db).WriteAsync(e);

        var officer = Guid.NewGuid();
        await using (var db = AppContext(tenantId))
        {
            var row = await db.SecurityEvents.SingleAsync(x => x.Id == e.Id);
            row.Acknowledge(officer, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        await using var read = AppContext(tenantId);
        var loaded = await read.SecurityEvents.AsNoTracking().SingleAsync(x => x.Id == e.Id);
        loaded.AcknowledgedBy.Should().Be(officer);
        loaded.Acknowledge(officer, DateTimeOffset.UtcNow).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Content_cannot_be_smuggled_into_the_details()
    {
        var result = SecurityEvent.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, Severity.Low, "x.y",
            details: new Dictionary<string, string> { ["text"] = "Patient John Smith" });

        result.IsFailure.Should().BeTrue();
    }
}
