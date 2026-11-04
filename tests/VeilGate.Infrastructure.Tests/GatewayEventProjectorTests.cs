using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Messaging;
using VeilGate.Domain.Metrics;
using VeilGate.Infrastructure.Messaging;
using VeilGate.Infrastructure.Persistence;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Infrastructure.Tests;

[Collection("postgres")]
public class GatewayEventProjectorTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 10, 42, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hour = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private GatewayEventProjector Projector()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<VeilGateDbContext>((sp, o) => o.UseNpgsql(pg.AppConnection).AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));
        return new GatewayEventProjector(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new FixedClock(),
            NullLogger<GatewayEventProjector>.Instance);
    }

    private async Task<Guid> SeedTenant()
    {
        var t = Tenant.Create($"Projector {Guid.NewGuid():N}"[..20]).Value;
        await using var db = pg.AdminContext();
        db.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static string Json(Guid tenantId, Guid clientId, string result = "success", Dictionary<string, int>? entities = null, long latencyMs = 80, DateTimeOffset? at = null) =>
        JsonSerializer.Serialize(GatewayRequestEvent.Create("req_1", at ?? At, tenantId, clientId, "client", "gateway.chat", result,
            entities ?? new Dictionary<string, int> { ["PHONE"] = 2 }, latencyMs).Value, JsonSerializerOptions.Web);

    private async Task<List<MetricsHour>> Hours(Guid tenantId)
    {
        await using var db = pg.AdminContext();   // the owner sees every row
        return await db.MetricsHourly.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();
    }

    private async Task<int> Requests(Guid tenantId)
    {
        await using var db = pg.AdminContext();
        return await db.GatewayRequests.CountAsync(x => x.TenantId == tenantId);
    }

    private async Task<int> Processed(Guid tenantId)
    {
        await using var db = pg.AdminContext();
        return await db.ProcessedMessages.CountAsync(x => x.TenantId == tenantId);
    }

    [Fact]
    public async Task A_new_message_writes_the_request_row_and_the_hourly_counters()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var outcome = await Projector().ProjectAsync(messageId, Json(tenantId, clientId, entities: new() { ["PHONE"] = 2, ["SSN"] = 1 }));

        outcome.Should().Be(ProjectionOutcome.Applied);
        await using var db = pg.AdminContext();
        var row = await db.GatewayRequests.AsNoTracking().SingleAsync(x => x.TenantId == tenantId);
        row.Id.Should().Be(messageId);
        row.EntityCount.Should().Be(3);
        row.CreatedAt.Should().Be(At);

        var hour = (await Hours(tenantId)).Should().ContainSingle().Subject;
        hour.Hour.Should().Be(Hour);
        hour.ClientId.Should().Be(clientId);
        (hour.Requests, hour.Blocked, hour.Failed, hour.Entities).Should().Be((1, 0, 0, 3));
        hour.EntitiesByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 2, ["SSN"] = 1 });
        hour.LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 1 });
        (await Processed(tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task The_same_message_delivered_twice_is_counted_once()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var json = Json(tenantId, clientId);
        var projector = Projector();

        (await projector.ProjectAsync(messageId, json)).Should().Be(ProjectionOutcome.Applied);
        (await projector.ProjectAsync(messageId, json)).Should().Be(ProjectionOutcome.Duplicate);

        (await Hours(tenantId)).Should().ContainSingle().Which.Requests.Should().Be(1);
        (await Requests(tenantId)).Should().Be(1);
        (await Processed(tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task Different_messages_in_one_hour_add_up()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var projector = Projector();

        await projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientId, "success", new() { ["PHONE"] = 2 }, 80));
        await projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientId, "denied", new() { ["PHONE"] = 1, ["SSN"] = 1 }, 90));
        await projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientId, "failure", new() { }, 400));

        var hour = (await Hours(tenantId)).Should().ContainSingle().Subject;
        (hour.Requests, hour.Blocked, hour.Failed, hour.Entities).Should().Be((3, 1, 1, 4));
        hour.EntitiesByType.Should().BeEquivalentTo(new Dictionary<string, int> { ["PHONE"] = 3, ["SSN"] = 1 });
        hour.LatencyBuckets.Should().BeEquivalentTo(new Dictionary<string, int> { ["100"] = 2, ["500"] = 1 });
    }

    [Fact]
    public async Task Another_hour_or_another_client_gets_its_own_row()
    {
        var tenantId = await SeedTenant();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var projector = Projector();

        await projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientA));
        await projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientA, at: At.AddHours(1)));
        await projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientB));

        (await Hours(tenantId)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Tenants_are_counted_separately()
    {
        var a = await SeedTenant();
        var b = await SeedTenant();
        var projector = Projector();

        await projector.ProjectAsync(Guid.NewGuid(), Json(a, Guid.NewGuid()));
        await projector.ProjectAsync(Guid.NewGuid(), Json(a, Guid.NewGuid()));
        await projector.ProjectAsync(Guid.NewGuid(), Json(b, Guid.NewGuid()));

        (await Hours(a)).Sum(h => h.Requests).Should().Be(2);
        (await Hours(b)).Sum(h => h.Requests).Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task A_message_that_is_not_a_gateway_event_is_rejected_and_writes_nothing(string payload)
    {
        var messageId = Guid.NewGuid();

        var outcome = await Projector().ProjectAsync(messageId, payload);

        outcome.Should().Be(ProjectionOutcome.Rejected);
        await using var db = pg.AdminContext();
        (await db.ProcessedMessages.CountAsync(x => x.MessageId == messageId)).Should().Be(0);
    }

    [Fact]
    public async Task A_failure_in_the_middle_leaves_nothing_behind_so_the_message_can_be_tried_again()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        // A request row with the same id is already there, so step 2 (adding the request row) fails after step 1 (recording the message).
        await using (var admin = pg.AdminContext())
        {
            admin.GatewayRequests.Add(GatewayRequestRecord.From(messageId, GatewayRequestEvent.Parse(Json(tenantId, clientId)).Value));
            await admin.SaveChangesAsync();
        }

        var act = () => Projector().ProjectAsync(messageId, Json(tenantId, clientId));

        await act.Should().ThrowAsync<DbUpdateException>();
        (await Processed(tenantId)).Should().Be(0);   // the message was not marked as handled
        (await Hours(tenantId)).Should().BeEmpty();   // and no counters were touched
    }

    [Fact]
    public async Task Many_messages_arriving_at_once_for_one_hour_lose_no_update()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var projector = Projector();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            projector.ProjectAsync(Guid.NewGuid(), Json(tenantId, clientId, entities: new() { ["PHONE"] = 1 }))));

        outcomes.Should().OnlyContain(o => o == ProjectionOutcome.Applied);
        var hour = (await Hours(tenantId)).Should().ContainSingle().Subject;
        (hour.Requests, hour.Entities).Should().Be((20, 20));
        (await Requests(tenantId)).Should().Be(20);
    }

    [Fact]
    public async Task The_same_message_arriving_five_times_at_once_is_counted_once()
    {
        var tenantId = await SeedTenant();
        var clientId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var json = Json(tenantId, clientId);
        var projector = Projector();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => projector.ProjectAsync(messageId, json)));

        outcomes.Count(o => o == ProjectionOutcome.Applied).Should().Be(1);
        outcomes.Count(o => o == ProjectionOutcome.Duplicate).Should().Be(4);
        (await Hours(tenantId)).Should().ContainSingle().Which.Requests.Should().Be(1);
    }
}
