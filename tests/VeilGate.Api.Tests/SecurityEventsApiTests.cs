using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Infrastructure.Security;
using VeilGate.TestSupport;
using Xunit;

namespace VeilGate.Api.Tests;

/// <summary>
/// Security events from the outside: who sees what by role, the user filter, severity, paging, names and tenant isolation.
/// Real HTTP, real database and row-level security, real tokens.
/// </summary>
[Collection("postgres")]
public sealed class SecurityEventsApiTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _pg;
    private readonly GatewayApiFactory _factory;
    private readonly HttpClient _client;

    public SecurityEventsApiTests(PostgresFixture pg)
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

    private async Task<Guid> SeedTenant()
    {
        var tenant = Tenant.Create($"Sec {Guid.NewGuid():N}"[..16]).Value;
        await using var db = _pg.AdminContext();
        db.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

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

    private async Task AddEvent(Guid tenantId, Severity severity, string type, DateTimeOffset at, Guid? userId = null, Guid? clientId = null)
    {
        var made = SecurityEvent.Create(tenantId, at, severity, type, userId: userId, clientId: clientId, ip: "203.0.113.9");
        await using var db = _pg.AdminContext();
        db.Add(made.Value);
        await db.SaveChangesAsync();
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

    private static IEnumerable<string> Types(IEnumerable<JsonElement> items) => items.Select(i => i.GetProperty("type").GetString()!);

    /// <summary>A tenant with an admin, a developer and a stranger, and five events about different people and one about a client.</summary>
    private sealed record Setup(Guid TenantId, User Admin, User Developer, User Stranger);

    private async Task<Setup> NewTenant()
    {
        var tenantId = await SeedTenant();
        var admin = await SeedUser(tenantId, UserRole.TenantAdmin);
        var developer = await SeedUser(tenantId, UserRole.Developer);
        var stranger = await SeedUser(tenantId, UserRole.Auditor);

        await AddEvent(tenantId, Severity.Medium, "auth.account_locked", Day1, userId: developer.Id);
        await AddEvent(tenantId, Severity.Medium, "mfa.reset", Day1.AddHours(1), userId: developer.Id);
        await AddEvent(tenantId, Severity.Medium, "auth.account_locked", Day1.AddHours(2), userId: stranger.Id);
        await AddEvent(tenantId, Severity.High, "leak.gate_tripped", Day1.AddHours(3), userId: admin.Id);
        await AddEvent(tenantId, Severity.Low, "oauth.invalid_secret", Day1.AddHours(4), clientId: Guid.NewGuid());
        return new Setup(tenantId, admin, developer, stranger);
    }

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task Every_person_of_the_tenant_may_ask_and_nobody_else()
    {
        var s = await NewTenant();

        (await Get("/api/v1/security-events", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/api/v1/security-events", ClientToken(s.TenantId))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var role in new[] { UserRole.TenantAdmin, UserRole.SecurityOfficer, UserRole.Developer, UserRole.Auditor })
            (await Get("/api/v1/security-events", UserToken(await SeedUser(s.TenantId, role)))).StatusCode.Should().Be(HttpStatusCode.OK, role.ToString());
    }

    // ---------------------------------------------------------------- what each role sees

    [Theory]
    [InlineData(UserRole.TenantAdmin)]
    [InlineData(UserRole.SecurityOfficer)]
    public async Task Tenant_admins_and_security_officers_see_the_whole_organization(UserRole role)
    {
        var s = await NewTenant();
        var viewer = await SeedUser(s.TenantId, role);

        var items = await Items("/api/v1/security-events", UserToken(viewer));

        items.Should().HaveCount(5);
        Types(items).Should().Contain(["auth.account_locked", "mfa.reset", "leak.gate_tripped", "oauth.invalid_secret"]);
    }

    [Theory]
    [InlineData(UserRole.Developer)]
    [InlineData(UserRole.Auditor)]
    public async Task Everyone_else_sees_only_events_about_themselves(UserRole role)
    {
        var s = await NewTenant();
        var me = role == UserRole.Developer ? s.Developer : s.Stranger;

        var items = await Items("/api/v1/security-events", UserToken(me));

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.GetProperty("userId").GetGuid() == me.Id);
    }

    [Fact]
    public async Task Everyone_else_cannot_ask_for_another_persons_events()
    {
        var s = await NewTenant();

        var response = await Get($"/api/v1/security-events?userId={s.Stranger.Id}", UserToken(s.Developer));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(response)).GetProperty("code").GetString().Should().Be("security_events.own_only");
    }

    [Fact]
    public async Task Everyone_else_may_name_themselves_in_the_filter()
    {
        var s = await NewTenant();

        var items = await Items($"/api/v1/security-events?userId={s.Developer.Id}", UserToken(s.Developer));

        items.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------- filters, order, names

    [Fact]
    public async Task An_admin_can_filter_by_person_and_by_severity()
    {
        var s = await NewTenant();
        var token = UserToken(s.Admin);

        var byPerson = await Items($"/api/v1/security-events?userId={s.Developer.Id}", token);
        var bySeverity = await Items("/api/v1/security-events?severity=high", token);

        byPerson.Should().HaveCount(2);
        Types(bySeverity).Should().Equal("leak.gate_tripped");
    }

    [Fact]
    public async Task An_unknown_severity_is_a_validation_error()
    {
        var s = await NewTenant();

        (await Get("/api/v1/security-events?severity=urgent", UserToken(s.Admin))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Events_come_newest_first_with_the_names_of_who_they_are_about()
    {
        var s = await NewTenant();

        var items = await Items("/api/v1/security-events", UserToken(s.Admin));

        Types(items).First().Should().Be("oauth.invalid_secret");
        items.Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).Should().BeInDescendingOrder();
        var locked = items.First(i => i.GetProperty("userId").ValueKind == JsonValueKind.String
            && i.GetProperty("userId").GetGuid() == s.Developer.Id && i.GetProperty("type").GetString() == "auth.account_locked");
        locked.GetProperty("userName").GetString().Should().Contain(s.Developer.Email);
        locked.GetProperty("severity").GetString().Should().Be("Medium");
        locked.GetProperty("ip").GetString().Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task Paging_splits_the_list()
    {
        var s = await NewTenant();
        var token = UserToken(s.Admin);

        var first = await Json(await Get("/api/v1/security-events?pageSize=2&page=1", token));
        var last = await Json(await Get("/api/v1/security-events?pageSize=2&page=3", token));

        first.GetProperty("total").GetInt32().Should().Be(5);
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        last.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---------------------------------------------------------------- the people list

    [Fact]
    public async Task The_people_list_is_for_admins_and_security_officers_only()
    {
        var s = await NewTenant();

        (await Get("/api/v1/security-events/people", UserToken(s.Developer))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get("/api/v1/security-events/people", UserToken(s.Stranger))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var people = await Items("/api/v1/security-events/people", UserToken(await SeedUser(s.TenantId, UserRole.SecurityOfficer)));
        people.Select(p => p.GetProperty("id").GetGuid()).Should().Contain([s.Admin.Id, s.Developer.Id, s.Stranger.Id]);
    }

    // ---------------------------------------------------------------- other organizations

    [Fact]
    public async Task Another_organizations_events_and_people_are_never_visible()
    {
        var mine = await NewTenant();
        var theirs = await NewTenant();
        var token = UserToken(mine.Admin);

        var events = await Items("/api/v1/security-events?pageSize=100", token);
        var people = await Items("/api/v1/security-events/people", token);

        events.Should().HaveCount(5);
        people.Select(p => p.GetProperty("id").GetGuid()).Should().NotContain([theirs.Admin.Id, theirs.Developer.Id]);
        (await Get($"/api/v1/security-events?userId={theirs.Developer.Id}", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Items($"/api/v1/security-events?userId={theirs.Developer.Id}", token)).Should().BeEmpty();
    }
}
