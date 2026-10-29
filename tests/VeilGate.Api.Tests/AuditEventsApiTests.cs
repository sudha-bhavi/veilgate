using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Infrastructure.Persistence;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>The audit log viewer from the outside: who may read it, filters, paging, names, tenant isolation and the chain check.</summary>
[Collection("postgres")]
public sealed class AuditEventsApiTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);
    private static readonly DateTimeOffset Day3 = Day1.AddDays(2);

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;

    public AuditEventsApiTests(PostgresFixture pg)
    {
        _pg = pg;
        _factory = new GatewayApiFactory(pg);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Setup(Guid TenantId, User Admin, string AdminToken, Guid PolicyId);

    private async Task<User> SeedUser(Guid tenantId, UserRole role)
    {
        var sfx = Guid.NewGuid().ToString("N")[..10];
        var user = User.Invite(tenantId, $"user-{sfx}@example.test", $"Person {sfx}", role, DateTimeOffset.UtcNow).Value;
        user.AcceptInvitation(new Argon2PasswordHasher().Hash("correct horse battery staple"), DateTimeOffset.UtcNow);
        await using var db = _pg.AdminContext();
        db.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private string UserToken(User u) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(
            new AccessTokenClaims(u.Id, u.TenantId, u.Role.ToString(), Guid.NewGuid(), ["pwd", "otp"], DateTimeOffset.UtcNow, u.TokenVersion));

    private string ClientToken(Guid tenantId) =>
        _factory.Services.GetRequiredService<ITokenService>().IssueClientToken(new ClientTokenClaims(Guid.NewGuid(), tenantId, ["gateway:detect"]));

    /// <summary>
    /// A new tenant with an admin and four events written through the real hash-chain writer:
    /// day 1 policy.create (success), day 2 policy.publish (denied), day 3 gateway.detect by a client, day 3 a system event.
    /// </summary>
    private async Task<Setup> NewTenant()
    {
        var tenant = Tenant.Create($"Audit {Guid.NewGuid():N}"[..16]).Value;
        await using (var admin = _pg.AdminContext())
        {
            admin.Add(tenant);
            await admin.SaveChangesAsync();
        }
        var person = await SeedUser(tenant.Id, UserRole.TenantAdmin);
        var policyId = Guid.NewGuid();

        await using (var db = _pg.AdminContext())
        {
            var writer = new HashChainAuditWriter(db);
            await writer.WriteAsync(AuditEvent.Create(tenant.Id, Day1, "user", person.Id, "policy.create", "policy", policyId,
                ip: "203.0.113.5", details: new Dictionary<string, string> { ["version"] = "1" }).Value);
            await writer.WriteAsync(AuditEvent.Create(tenant.Id, Day2, "user", person.Id, "policy.publish", "policy", policyId,
                result: "denied").Value);
            await writer.WriteAsync(AuditEvent.Create(tenant.Id, Day3, "client", Guid.NewGuid(), "gateway.detect").Value);
            await writer.WriteAsync(AuditEvent.Create(tenant.Id, Day3.AddMinutes(1), "system", null, "vault.purge").Value);
        }
        return new Setup(tenant.Id, person, UserToken(person), policyId);
    }

    private async Task<HttpResponseMessage> Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<List<JsonElement>> Items(string path, string token)
    {
        var response = await Get(path, token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private static IEnumerable<string> Actions(IEnumerable<JsonElement> items) => items.Select(i => i.GetProperty("action").GetString()!);

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task Admins_security_officers_and_auditors_may_read_the_log_and_nobody_else()
    {
        var s = await NewTenant();

        (await Get("/api/v1/audit-events", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/api/v1/audit-events", UserToken(await SeedUser(s.TenantId, UserRole.Developer)))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/v1/audit-events", ClientToken(s.TenantId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/v1/audit-events/verify", ClientToken(s.TenantId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        foreach (var role in new[] { UserRole.TenantAdmin, UserRole.SecurityOfficer, UserRole.Auditor })
            (await Get("/api/v1/audit-events", UserToken(await SeedUser(s.TenantId, role)))).StatusCode.Should().Be(HttpStatusCode.OK, role.ToString());
    }

    [Fact]
    public async Task The_log_cannot_be_changed_through_the_api()
    {
        var s = await NewTenant();
        var first = (await Items("/api/v1/audit-events", s.AdminToken)).Last().GetProperty("id").GetGuid();

        foreach (var method in new[] { HttpMethod.Delete, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Post })
        {
            var request = new HttpRequestMessage(method, $"/api/v1/audit-events/{first}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AdminToken);
            ((int)(await _client.SendAsync(request)).StatusCode).Should().BeOneOf(404, 405);
        }
    }

    // ---------------------------------------------------------------- listing

    [Fact]
    public async Task Events_come_newest_first_with_the_actor_name_and_without_any_way_to_see_another_tenant()
    {
        var s = await NewTenant();
        var other = await NewTenant();

        var response = await Get("/api/v1/audit-events", s.AdminToken);
        var body = await Json(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        body.GetProperty("total").GetInt32().Should().Be(4);
        Actions(items).Should().Equal("vault.purge", "gateway.detect", "policy.publish", "policy.create");
        items.Select(i => i.GetProperty("seq").GetInt64()).Should().Equal(4, 3, 2, 1);

        var created = items[3];
        created.GetProperty("actorName").GetString().Should().Be($"{s.Admin.DisplayName} ({s.Admin.Email})");
        created.GetProperty("resourceId").GetGuid().Should().Be(s.PolicyId);
        created.GetProperty("ip").GetString().Should().Be("203.0.113.5");
        created.GetProperty("details").GetProperty("version").GetString().Should().Be("1");
        created.GetProperty("hash").GetString().Should().HaveLength(64);
        items[0].GetProperty("actorName").ValueKind.Should().Be(JsonValueKind.Null);   // the system has no name

        // The other tenant's own four events, none of these.
        var theirs = await Items("/api/v1/audit-events", other.AdminToken);
        theirs.Select(i => i.GetProperty("id").GetGuid()).Should().NotIntersectWith(items.Select(i => i.GetProperty("id").GetGuid()));
        (await Items($"/api/v1/audit-events?resourceId={s.PolicyId}", other.AdminToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Filters_narrow_the_log_and_combine()
    {
        var s = await NewTenant();

        Actions(await Items("/api/v1/audit-events?action=policy.publish", s.AdminToken)).Should().Equal("policy.publish");
        Actions(await Items("/api/v1/audit-events?result=denied", s.AdminToken)).Should().Equal("policy.publish");
        Actions(await Items("/api/v1/audit-events?actorType=client", s.AdminToken)).Should().Equal("gateway.detect");
        Actions(await Items($"/api/v1/audit-events?actorId={s.Admin.Id}", s.AdminToken)).Should().Equal("policy.publish", "policy.create");
        Actions(await Items($"/api/v1/audit-events?resourceType=policy&resourceId={s.PolicyId}&result=success", s.AdminToken))
            .Should().Equal("policy.create");

        // A time range, including one written with an offset: 2026-10-02T11:00+02:00 is 09:00 UTC on day 2.
        var from = Uri.EscapeDataString("2026-10-02T11:00:00+02:00");
        var to = Uri.EscapeDataString(Day2.AddHours(1).ToString("O"));
        Actions(await Items($"/api/v1/audit-events?from={from}&to={to}", s.AdminToken)).Should().Equal("policy.publish");
    }

    [Fact]
    public async Task Pages_hold_the_requested_number_of_events_and_report_the_total()
    {
        var s = await NewTenant();

        var second = await Json(await Get("/api/v1/audit-events?page=2&pageSize=3", s.AdminToken));

        second.GetProperty("total").GetInt32().Should().Be(4);
        second.GetProperty("page").GetInt32().Should().Be(2);
        Actions(second.GetProperty("items").EnumerateArray()).Should().Equal("policy.create");
    }

    [Fact]
    public async Task A_filter_that_can_never_match_is_refused_with_a_reason()
    {
        var s = await NewTenant();

        foreach (var query in new[] { "result=maybe", "actorType=robot", $"from={Day3:O}&to={Day1:O}" })
        {
            var response = await Get($"/api/v1/audit-events?{query.Replace("+", "%2B")}", s.AdminToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
            (await Json(response)).GetProperty("code").GetString().Should().Be("validation.failed", query);
        }

        (await Get("/api/v1/audit-events?from=yesterday", s.AdminToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_action_names_of_the_tenant_are_listed_once_each_in_order()
    {
        var s = await NewTenant();

        var actions = await Items("/api/v1/audit-events/actions", s.AdminToken);

        actions.Select(a => a.GetString()).Should().Equal("gateway.detect", "policy.create", "policy.publish", "vault.purge");
    }

    // ---------------------------------------------------------------- the chain

    [Fact]
    public async Task The_chain_check_recomputes_every_event_of_the_tenant()
    {
        var s = await NewTenant();

        var check = await Json(await Get("/api/v1/audit-events/verify", UserToken(await SeedUser(s.TenantId, UserRole.Auditor))));

        check.GetProperty("ok").GetBoolean().Should().BeTrue();
        check.GetProperty("verified").GetInt64().Should().Be(4);
        check.GetProperty("brokenAtSeq").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
